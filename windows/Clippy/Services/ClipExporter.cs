using System.Diagnostics;
using System.Globalization;
using System.Text;
using Clippy.Models;

namespace Clippy.Services;

public static class ClipExporter
{
    public static async Task<bool> IsPlayableVideoAsync(string path) =>
        await Task.Run(() => HasReadableVideoSync(path));

    public static bool HasReadableVideoSync(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 500;

    /// <summary>
    /// Concatenates <paramref name="segments"/> and keeps the <em>last</em>
    /// <paramref name="trimToSeconds"/> seconds. Returns the exported duration.
    /// </summary>
    public static async Task<double> ExportAsync(
        IReadOnlyList<RecordingSegment> segments,
        double trimToSeconds,
        string outputPath)
    {
        var valid = segments.Where(s => HasReadableVideoSync(s.Path)).OrderBy(s => s.Index).ToList();
        if (valid.Count == 0)
        {
            throw new InvalidOperationException(
                "Could not read video from the buffer — wait a few seconds and try again.");
        }

        if (File.Exists(outputPath)) File.Delete(outputPath);

        var listFile = Path.Combine(Path.GetTempPath(), $"clippy_concat_{Guid.NewGuid():N}.txt");
        var sb = new StringBuilder();
        foreach (var segment in valid)
        {
            // The concat demuxer treats a single quote as a terminator; this is its escape form.
            sb.Append("file '").Append(segment.Path.Replace("'", @"'\''")).AppendLine("'");
        }

        await File.WriteAllTextAsync(listFile, sb.ToString());

        try
        {
            // Only the newest segment has an unknown length. Measuring that one file is a
            // sub-5-second decode; measuring the whole concatenation would decode the entire
            // buffer, adding seconds to every clip for a number we can work out instead.
            var totalDuration = 0.0;
            foreach (var segment in valid)
            {
                totalDuration += segment.IsComplete
                    ? FfmpegSegmentRecorder.SegmentSeconds
                    : await MeasureDurationAsync(segment.Path) ?? 0;
            }

            if (totalDuration <= 0.01)
            {
                throw new InvalidOperationException(
                    "The buffer has no usable footage yet — wait a few seconds and try again.");
            }

            var keepSeconds = Math.Min(trimToSeconds, totalDuration);

            // Take the tail, not the head — the interesting moment is what just happened.
            var startSeconds = Math.Max(0, totalDuration - keepSeconds);

            var seek = startSeconds > 0.01
                ? $"-ss {startSeconds.ToString("F3", CultureInfo.InvariantCulture)} "
                : "";

            // Re-encode rather than stream-copy: the cut point is rarely on a keyframe, and
            // copying would otherwise begin the clip with a stretch of corrupt frames.
            var args =
                $"-hide_banner -loglevel error -y -f concat -safe 0 {seek}-i \"{listFile}\" " +
                $"-t {keepSeconds.ToString("F3", CultureInfo.InvariantCulture)} " +
                "-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p " +
                $"-c:a aac -b:a 192k -movflags +faststart \"{outputPath}\"";

            await RunFfmpegAsync(args);

            if (!HasReadableVideoSync(outputPath))
            {
                throw new InvalidOperationException("Export produced an unplayable clip — try again.");
            }

            return await MeasureDurationAsync(outputPath) ?? keepSeconds;
        }
        finally
        {
            try { File.Delete(listFile); } catch { }
        }
    }

    private static Task<double?> MeasureDurationAsync(string path) =>
        MeasureAsync($"-hide_banner -i \"{path}\"");

    /// <summary>
    /// Reads a duration out of FFmpeg's own stream report, so no ffprobe binary is needed
    /// alongside the bundled ffmpeg.
    /// </summary>
    private static async Task<double?> MeasureAsync(string inputArgs)
    {
        try
        {
            var (_, stderr) = await RunProcessAsync($"{inputArgs} -f null -");
            var marker = stderr.LastIndexOf("time=", StringComparison.Ordinal);
            if (marker < 0) return null;

            var value = stderr[(marker + 5)..].Split(' ', '\r', '\n')[0].Trim();
            var parts = value.Split(':');
            if (parts.Length != 3) return null;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return null;
            }

            var total = hours * 3600 + minutes * 60 + seconds;
            return total > 0.01 ? total : null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task RunFfmpegAsync(string arguments)
    {
        var (exitCode, stderr) = await RunProcessAsync(arguments);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? "Failed to export the clip."
                : stderr.Trim());
        }
    }

    private static async Task<(int ExitCode, string Stderr)> RunProcessAsync(string arguments)
    {
        var ffmpeg = FfmpegLocator.Path
            ?? throw new InvalidOperationException(
                "FFmpeg was not found. Reinstall Clippy, or place ffmpeg.exe next to Clippy.exe.");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("Failed to start FFmpeg.");

        // Read both pipes before waiting. Waiting first deadlocks as soon as FFmpeg writes
        // more than the pipe buffer holds, which it does on any non-trivial export.
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();

        await Task.WhenAll(stderrTask, stdoutTask);
        await process.WaitForExitAsync();

        return (process.ExitCode, await stderrTask);
    }
}

public static class FfmpegLocator
{
    private static string? _cached;
    private static bool _resolved;
    private static readonly object Gate = new();

    public static string? Path
    {
        get
        {
            lock (Gate)
            {
                if (_resolved) return _cached;
                _resolved = true;
                _cached = Resolve();
                return _cached;
            }
        }
    }

    public static bool IsBundled =>
        Path != null &&
        string.Equals(Path, BundledPath, StringComparison.OrdinalIgnoreCase);

    private static string BundledPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");

    private static string? Resolve()
    {
        if (File.Exists(BundledPath))
        {
            ClippyDebugLog.Instance.Log("Recorder", $"Using bundled FFmpeg: {BundledPath}");
            return BundledPath;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = System.IO.Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    ClippyDebugLog.Instance.Log("Recorder", $"Using FFmpeg from PATH: {candidate}");
                    return candidate;
                }
            }
            catch
            {
                // Malformed PATH entries are not worth failing over.
            }
        }

        const string common = @"C:\ffmpeg\bin\ffmpeg.exe";
        if (File.Exists(common))
        {
            ClippyDebugLog.Instance.Log("Recorder", $"Using FFmpeg from {common}");
            return common;
        }

        ClippyDebugLog.Instance.Log("Recorder", "FFmpeg not found");
        return null;
    }
}
