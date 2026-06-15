using System.Diagnostics;
using System.Text;
using Clippy.Models;

namespace Clippy.Services;

public sealed class FfmpegSegmentRecorder : IDisposable
{
    private Process? _process;
    private CancellationTokenSource? _rotationCts;
    private Task? _rotationTask;
    private readonly object _lock = new();

    public event Action<RecordingSegment>? SegmentFinished;

    public bool IsRunning => _process is { HasExited: false };

    public void Start(CaptureSettings settings, string bufferDirectory)
    {
        Stop();

        _rotationCts = new CancellationTokenSource();
        _rotationTask = Task.Run(() => RotationLoop(settings, bufferDirectory, _rotationCts.Token));
    }

    public void Stop()
    {
        _rotationCts?.Cancel();
        try { _rotationTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _rotationCts?.Dispose();
        _rotationCts = null;
        _rotationTask = null;

        lock (_lock)
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch { }

            _process?.Dispose();
            _process = null;
        }
    }

    private async Task RotationLoop(CaptureSettings settings, string bufferDirectory, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var segmentPath = Path.Combine(bufferDirectory, $"seg_{Guid.NewGuid():N}.mp4");
            try
            {
                var recorded = await RecordSegmentWithFallbackAsync(settings, segmentPath, 5, token);
                if (recorded && File.Exists(segmentPath) && new FileInfo(segmentPath).Length > 500)
                {
                    SegmentFinished?.Invoke(new RecordingSegment
                    {
                        Path = segmentPath,
                        StartTime = DateTime.Now.AddSeconds(-5),
                        Duration = 5,
                        FrameCount = (int)(settings.FrameRate * 5)
                    });
                }
                else
                {
                    try { File.Delete(segmentPath); } catch { }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ClippyDebugLog.Instance.Log("Recorder", $"Segment error: {ex.Message}");
                await Task.Delay(1000, token);
            }
        }
    }

    private async Task<bool> RecordSegmentWithFallbackAsync(
        CaptureSettings settings,
        string outputPath,
        int seconds,
        CancellationToken token)
    {
        var attempts = new List<(bool systemAudio, bool mic)>
        {
            (true, !string.IsNullOrEmpty(settings.MicrophoneDeviceName)),
            (true, false),
            (false, false)
        };

        foreach (var (systemAudio, mic) in attempts)
        {
            if (File.Exists(outputPath))
            {
                try { File.Delete(outputPath); } catch { }
            }

            var args = BuildArgs(settings, outputPath, seconds, systemAudio, mic);
            var exitCode = await RunFfmpegAsync(args, token);
            if (exitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 500)
            {
                if (!mic && !string.IsNullOrEmpty(settings.MicrophoneDeviceName))
                {
                    ClippyDebugLog.Instance.Log("Recorder", "Recording without microphone — check mic device in Settings");
                }

                return true;
            }
        }

        return false;
    }

    private static string BuildArgs(
        CaptureSettings settings,
        string outputPath,
        int seconds,
        bool includeSystemAudio,
        bool includeMic)
    {
        var (width, height) = settings.Dimensions;
        var fps = settings.FrameRate;
        var bitrate = settings.VideoBitrate;

        var argsBuilder = new StringBuilder();
        argsBuilder.Append($"-y -f gdigrab -framerate {fps} -draw_mouse 1 ");
        argsBuilder.Append($"-offset_x {settings.OffsetX} -offset_y {settings.OffsetY} ");
        argsBuilder.Append($"-video_size {width}x{height} -i desktop ");

        var inputCount = 1;
        if (includeSystemAudio)
        {
            var audioInput = string.IsNullOrWhiteSpace(settings.OutputDeviceName)
                ? "default"
                : $"audio=\"{EscapeDeviceName(settings.OutputDeviceName)}\"";
            argsBuilder.Append($"-f wasapi -loopback 1 -i {audioInput} ");
            inputCount++;
        }

        if (includeMic && !string.IsNullOrEmpty(settings.MicrophoneDeviceName))
        {
            argsBuilder.Append($"-f dshow -i audio=\"{EscapeDshow(settings.MicrophoneDeviceName)}\" ");
            inputCount++;
        }

        if (inputCount == 1)
        {
            return argsBuilder.ToString() +
                   $"-map 0:v -c:v libx264 -preset ultrafast -b:v {bitrate} -pix_fmt yuv420p -an " +
                   $"-t {seconds} \"{outputPath}\"";
        }

        if (inputCount == 2)
        {
            return argsBuilder.ToString() +
                   "-filter_complex \"[1:a]volume=1.0[a0]\" " +
                   $"-map 0:v -map \"[a0]\" -c:v libx264 -preset ultrafast -b:v {bitrate} -pix_fmt yuv420p " +
                   $"-c:a aac -b:a 192k -t {seconds} \"{outputPath}\"";
        }

        return argsBuilder.ToString() +
               "-filter_complex \"[1:a]volume=1.0[a0];[2:a]volume=1.25[a1];[a0][a1]amix=inputs=2:duration=first[aout]\" " +
               $"-map 0:v -map \"[aout]\" -c:v libx264 -preset ultrafast -b:v {bitrate} -pix_fmt yuv420p " +
               $"-c:a aac -b:a 192k -t {seconds} \"{outputPath}\"";
    }

    private async Task<int> RunFfmpegAsync(string arguments, CancellationToken token)
    {
        var ffmpeg = FfmpegLocator.Path
            ?? throw new InvalidOperationException("FFmpeg not found.");

        Process process;
        lock (_lock)
        {
            _process?.Dispose();
            process = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }) ?? throw new InvalidOperationException("Failed to start FFmpeg.");
            _process = process;
        }

        using var reg = token.Register(() =>
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { }
        });

        var stderr = await process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);

        if (process.ExitCode != 0)
        {
            ClippyDebugLog.Instance.Log("Recorder", $"FFmpeg exit {process.ExitCode}: {TrimStderr(stderr)}");
        }

        return process.ExitCode;
    }

    private static string TrimStderr(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return "(no stderr)";
        }

        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(4));
    }

    private static string EscapeDshow(string name) => name.Replace("\"", "\\\"");

    private static string EscapeDeviceName(string name) => name.Replace("\"", "\\\"");

    public void Dispose() => Stop();
}

public sealed class CaptureSettings
{
    public int OffsetX { get; init; }
    public int OffsetY { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int FrameRate { get; init; } = 30;
    public int VideoBitrate { get; init; } = 2_000_000;
    public string? MicrophoneDeviceName { get; init; }
    public string? OutputDeviceName { get; init; }

    public (int Width, int Height) Dimensions => (Width, Height);

    public static CaptureSettings FromAppSettings(CaptureDisplay display)
    {
        var settings = AppSettings.Instance;
        var (width, height) = CaptureResolutionExtensions.DimensionsFor(
            settings.CaptureResolution, display.Width, display.Height);

        string? micName = null;
        if (!string.IsNullOrEmpty(settings.PreferredMicrophoneId))
        {
            micName = AudioDeviceManager.ResolvedDeviceName(
                settings.PreferredMicrophoneId, NAudio.CoreAudioApi.DataFlow.Capture);
        }

        string? outputName = null;
        if (!string.IsNullOrEmpty(settings.PreferredAudioOutputId))
        {
            outputName = AudioDeviceManager.ResolvedDeviceName(
                settings.PreferredAudioOutputId, NAudio.CoreAudioApi.DataFlow.Render);
        }

        return new CaptureSettings
        {
            OffsetX = display.OffsetX,
            OffsetY = display.OffsetY,
            Width = width,
            Height = height,
            FrameRate = (int)settings.CaptureFrameRate,
            VideoBitrate = settings.CaptureResolution.VideoBitrate(),
            MicrophoneDeviceName = micName is "System Default" or "Unknown Device" ? null : micName,
            OutputDeviceName = outputName is "System Default" or "Unknown Device" ? null : outputName
        };
    }
}
