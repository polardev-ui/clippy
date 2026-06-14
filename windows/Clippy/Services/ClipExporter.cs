using System.Diagnostics;
using System.Text;
using Clippy.Models;

namespace Clippy.Services;

public static class ClipExporter
{
    public static async Task<bool> IsPlayableVideoAsync(string path)
    {
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (info.Length < 500) return false;
        return await Task.Run(() => info.Length > 500);
    }

    public static bool HasReadableVideoSync(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 500;

    public static async Task ExportAsync(
        IReadOnlyList<RecordingSegment> segments,
        double trimToSeconds,
        string outputPath)
    {
        if (segments.Count == 0)
        {
            throw new InvalidOperationException("Could not read video from the buffer — wait a few seconds and try again.");
        }

        var valid = segments.Where(s => HasReadableVideoSync(s.Path)).ToList();
        if (valid.Count == 0)
        {
            throw new InvalidOperationException("Could not read video from the buffer — wait a few seconds and try again.");
        }

        if (File.Exists(outputPath)) File.Delete(outputPath);

        var totalDuration = valid.Sum(s => s.Duration);
        var keepSeconds = Math.Min(trimToSeconds, totalDuration);
        var skipSeconds = Math.Max(0, totalDuration - keepSeconds);

        var listFile = Path.Combine(Path.GetTempPath(), $"clippy_concat_{Guid.NewGuid():N}.txt");
        var selected = new List<RecordingSegment>();
        var remaining = keepSeconds;
        for (var i = valid.Count - 1; i >= 0 && remaining > 0; i--)
        {
            selected.Insert(0, valid[i]);
            remaining -= valid[i].Duration;
        }

        var sb = new StringBuilder();
        foreach (var seg in selected)
        {
            sb.AppendLine($"file '{seg.Path.Replace("'", "'\\''")}'");
        }

        await File.WriteAllTextAsync(listFile, sb.ToString());

        try
        {
            var args = $"-y -f concat -safe 0 -i \"{listFile}\" -c copy -t {keepSeconds:F3} \"{outputPath}\"";
            await RunFfmpegAsync(args);

            if (!HasReadableVideoSync(outputPath))
            {
                throw new InvalidOperationException("Export produced an unplayable clip — try again.");
            }
        }
        finally
        {
            try { File.Delete(listFile); } catch { }
        }
    }

    public static async Task RunFfmpegAsync(string arguments)
    {
        var ffmpeg = FfmpegLocator.Path;
        if (ffmpeg == null)
        {
            throw new InvalidOperationException(
                "FFmpeg was not found. Install FFmpeg and add it to PATH, or place ffmpeg.exe next to Clippy.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start FFmpeg.");

        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            var err = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(err)
                ? "Failed to export the clip."
                : err.Trim());
        }
    }
}

public static class FfmpegLocator
{
    public static string? Path
    {
        get
        {
            var local = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;

            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = System.IO.Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }

            var common = @"C:\ffmpeg\bin\ffmpeg.exe";
            return File.Exists(common) ? common : null;
        }
    }
}
