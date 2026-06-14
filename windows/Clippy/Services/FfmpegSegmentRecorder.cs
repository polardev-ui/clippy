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
                await RecordSegmentAsync(settings, segmentPath, 5, token);
                if (File.Exists(segmentPath) && new FileInfo(segmentPath).Length > 500)
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

    private async Task RecordSegmentAsync(
        CaptureSettings settings,
        string outputPath,
        int seconds,
        CancellationToken token)
    {
        var ffmpeg = FfmpegLocator.Path
            ?? throw new InvalidOperationException("FFmpeg not found.");

        var (width, height) = settings.Dimensions;
        var fps = settings.FrameRate;
        var bitrate = settings.VideoBitrate;

        var argsBuilder = new StringBuilder();
        argsBuilder.Append($"-y -f gdigrab -framerate {fps} -offset_x {settings.OffsetX} -offset_y {settings.OffsetY} ");
        argsBuilder.Append($"-video_size {width}x{height} -i desktop ");
        argsBuilder.Append("-f wasapi -loopback 1 -i default ");

        var filter = "[1:a]volume=1.0[a0]";
        var mapAudio = "[a0]";

        if (!string.IsNullOrEmpty(settings.MicrophoneDeviceName))
        {
            argsBuilder.Append($"-f dshow -i audio=\"{EscapeDshow(settings.MicrophoneDeviceName)}\" ");
            filter = "[1:a]volume=1.0[a0];[2:a]volume=1.25[a1];[a0][a1]amix=inputs=2:duration=first[aout]";
            mapAudio = "[aout]";
        }

        var args =
            argsBuilder.ToString() +
            $"-filter_complex \"{filter}\" " +
            $"-map 0:v -map \"{mapAudio}\" -c:v libx264 -preset ultrafast -b:v {bitrate} -pix_fmt yuv420p " +
            $"-c:a aac -b:a 192k -t {seconds} \"{outputPath}\"";

        lock (_lock)
        {
            _process?.Dispose();
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            });
        }

        if (_process == null) return;

        using var reg = token.Register(() =>
        {
            try
            {
                if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            }
            catch { }
        });

        await _process.WaitForExitAsync(token);
    }

    private static string EscapeDshow(string name) => name.Replace("\"", "\\\"");

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

        return new CaptureSettings
        {
            Width = width,
            Height = height,
            FrameRate = (int)settings.CaptureFrameRate,
            VideoBitrate = settings.CaptureResolution.VideoBitrate(),
            MicrophoneDeviceName = micName == "System Default" ? null : micName
        };
    }
}
