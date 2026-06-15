using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Clippy.Services;

public static class FfmpegCapabilities
{
    private static bool _probed;
    private static bool _supportsWasapi = true;
    private static bool _supportsDshow = true;
    private static bool _supportsLoopbackFlag = true;
    private static string _versionSummary = "unknown";

    public static bool SupportsWasapi
    {
        get { EnsureProbed(); return _supportsWasapi; }
    }

    public static bool SupportsDshow
    {
        get { EnsureProbed(); return _supportsDshow; }
    }

    public static bool SupportsLoopbackFlag
    {
        get { EnsureProbed(); return _supportsLoopbackFlag; }
    }

    public static string VersionSummary
    {
        get { EnsureProbed(); return _versionSummary; }
    }

    public static void Reset()
    {
        _probed = false;
        _supportsWasapi = true;
        _supportsDshow = true;
        _supportsLoopbackFlag = true;
        _versionSummary = "unknown";
    }

    public static void DisableWasapi()
    {
        EnsureProbed();
        _supportsWasapi = false;
        _supportsLoopbackFlag = false;
    }

    public static void DisableLoopbackFlag()
    {
        EnsureProbed();
        _supportsLoopbackFlag = false;
    }

    public static void NoteRuntimeError(string stderr)
    {
        if (stderr.Contains("Unknown input format: 'wasapi'", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("Unknown input format: wasapi", StringComparison.OrdinalIgnoreCase))
        {
            DisableWasapi();
        }

        if (stderr.Contains("Unrecognized option 'loopback'", StringComparison.OrdinalIgnoreCase))
        {
            DisableLoopbackFlag();
        }
    }

    private static void EnsureProbed()
    {
        if (_probed)
        {
            return;
        }

        _probed = true;
        var ffmpeg = FfmpegLocator.Path;
        if (ffmpeg == null || !File.Exists(ffmpeg))
        {
            _supportsWasapi = false;
            _supportsDshow = false;
            _supportsLoopbackFlag = false;
            return;
        }

        var version = RunQuiet(ffmpeg, "-hide_banner -version");
        _versionSummary = version.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "unknown";

        var formats = RunQuiet(ffmpeg, "-hide_banner -formats");
        _supportsWasapi = Regex.IsMatch(formats, @"\bwasapi\b", RegexOptions.IgnoreCase);
        _supportsDshow = Regex.IsMatch(formats, @"\bdshow\b", RegexOptions.IgnoreCase);

        if (_supportsWasapi)
        {
            var help = RunQuiet(ffmpeg, "-hide_banner -h demuxer=wasapi");
            _supportsLoopbackFlag = help.Contains("loopback", StringComparison.OrdinalIgnoreCase) &&
                                    !help.Contains("Unknown demuxer", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            _supportsLoopbackFlag = false;
        }

        ClippyDebugLog.Instance.Log("Recorder",
            $"FFmpeg: {FfmpegLocator.Path} | {_versionSummary}");
        ClippyDebugLog.Instance.Log("Recorder",
            $"FFmpeg capabilities: wasapi={_supportsWasapi} dshow={_supportsDshow} loopbackFlag={_supportsLoopbackFlag}");

        if (!_supportsWasapi)
        {
            ClippyDebugLog.Instance.Log("Recorder",
                "Bundled FFmpeg lacks WASAPI — system audio capture disabled until you reinstall with the full FFmpeg build");
        }
    }

    private static string RunQuiet(string ffmpeg, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });

            if (process == null)
            {
                return "";
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(15));
            return stdout + stderr;
        }
        catch
        {
            return "";
        }
    }
}
