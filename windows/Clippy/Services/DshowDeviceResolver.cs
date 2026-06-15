using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Clippy.Services;

public static class DshowDeviceResolver
{
    private static readonly object Lock = new();
    private static IReadOnlyList<string>? _audioDevices;
    private static DateTime _listedUtc = DateTime.MinValue;

    public static void InvalidateCache()
    {
        lock (Lock)
        {
            _audioDevices = null;
            _listedUtc = DateTime.MinValue;
        }
    }

    public static string? ResolveMicrophoneName(string? preferredName)
    {
        if (string.IsNullOrWhiteSpace(preferredName))
        {
            return null;
        }

        var devices = ListAudioDevices();
        if (devices.Count == 0)
        {
            return preferredName.Trim();
        }

        var exact = devices.FirstOrDefault(d =>
            d.Equals(preferredName, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        var contains = devices.FirstOrDefault(d =>
            d.Contains(preferredName, StringComparison.OrdinalIgnoreCase) ||
            preferredName.Contains(d, StringComparison.OrdinalIgnoreCase));
        if (contains != null)
        {
            return contains;
        }

        var normalizedPreferred = Normalize(preferredName);
        var fuzzy = devices.FirstOrDefault(d => Normalize(d) == normalizedPreferred);
        return fuzzy ?? preferredName.Trim();
    }

    public static IReadOnlyList<string> MicrophoneCandidates(string? preferredName)
    {
        var resolved = ResolveMicrophoneName(preferredName);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return Array.Empty<string>();
        }

        var candidates = new List<string> { resolved };
        var devices = ListAudioDevices();
        foreach (var device in devices)
        {
            if (device.Equals(resolved, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (device.Contains(resolved, StringComparison.OrdinalIgnoreCase) ||
                resolved.Contains(device, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(device);
            }
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> ListAudioDevices()
    {
        lock (Lock)
        {
            if (_audioDevices != null && (DateTime.UtcNow - _listedUtc).TotalMinutes < 5)
            {
                return _audioDevices;
            }

            _audioDevices = ProbeAudioDevices();
            _listedUtc = DateTime.UtcNow;
            return _audioDevices;
        }
    }

    private static IReadOnlyList<string> ProbeAudioDevices()
    {
        var ffmpeg = FfmpegLocator.Path;
        if (ffmpeg == null || !FfmpegCapabilities.SupportsDshow)
        {
            return Array.Empty<string>();
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = "-hide_banner -list_devices true -f dshow -i dummy",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });

            if (process == null)
            {
                return Array.Empty<string>();
            }

            var output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(15));

            var devices = new List<string>();
            foreach (Match match in Regex.Matches(output, "\"([^\"]+)\"\\s*\\(audio\\)", RegexOptions.IgnoreCase))
            {
                devices.Add(match.Groups[1].Value);
            }

            if (devices.Count > 0)
            {
                ClippyDebugLog.Instance.Log("Recorder",
                    $"DirectShow audio devices: {string.Join(" | ", devices)}");
            }

            return devices;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string Normalize(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", "");
}
