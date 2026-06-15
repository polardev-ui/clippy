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
    private static bool? _wasapiSupportsLoopbackFlag;

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
                var (recorded, hadMic, usedMic) = await RecordSegmentWithFallbackAsync(settings, segmentPath, 5, token);
                if (recorded && File.Exists(segmentPath) && new FileInfo(segmentPath).Length > 500)
                {
                    if (hadMic && !usedMic)
                    {
                        ClippyDebugLog.Instance.Log("Recorder", "Recording without microphone — check mic device in Settings");
                    }

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

    private async Task<(bool recorded, bool hadMic, bool usedMic)> RecordSegmentWithFallbackAsync(
        CaptureSettings settings,
        string outputPath,
        int seconds,
        CancellationToken token)
    {
        var hadMic = !string.IsNullOrEmpty(settings.MicrophoneDeviceName);
        var audioInputs = BuildSystemAudioInputAttempts(settings.OutputDeviceName);
        var micPlans = hadMic ? new[] { true, false } : new[] { false };

        foreach (var audioInput in audioInputs)
        {
            foreach (var useMic in micPlans)
            {
                if (File.Exists(outputPath))
                {
                    try { File.Delete(outputPath); } catch { }
                }

                var args = BuildArgs(settings, outputPath, seconds, audioInput, useMic);
                var exitCode = await RunFfmpegAsync(args, token);
                if (exitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 500)
                {
                    return (true, hadMic, useMic);
                }
            }
        }

        if (File.Exists(outputPath))
        {
            try { File.Delete(outputPath); } catch { }
        }

        var videoOnlyArgs = BuildArgs(settings, outputPath, seconds, audioInput: null, includeMic: false);
        var videoExit = await RunFfmpegAsync(videoOnlyArgs, token);
        if (videoExit == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 500)
        {
            ClippyDebugLog.Instance.Log("Recorder", "System audio unavailable — clip will be video only until audio capture succeeds");
            return (true, hadMic, false);
        }

        return (false, hadMic, false);
    }

    private static IReadOnlyList<string> BuildSystemAudioInputAttempts(string? outputDeviceName)
    {
        var attempts = new List<string>();
        var device = string.IsNullOrWhiteSpace(outputDeviceName) ? null : outputDeviceName.Trim();

        if (_wasapiSupportsLoopbackFlag != false)
        {
            attempts.Add(WasapiInput(device, useLoopbackFlag: true));
            if (device != null)
            {
                attempts.Add(WasapiInput($"{device} (loopback)", useLoopbackFlag: true));
            }
        }

        if (device != null)
        {
            attempts.Add(WasapiInput(device, useLoopbackFlag: false));
            attempts.Add(WasapiInput($"{device} (loopback)", useLoopbackFlag: false));
        }

        attempts.Add(WasapiInput(null, useLoopbackFlag: false));
        if (_wasapiSupportsLoopbackFlag != false)
        {
            attempts.Add(WasapiInput(null, useLoopbackFlag: true));
        }

        return attempts.Distinct().ToList();
    }

    private static string WasapiInput(string? deviceName, bool useLoopbackFlag)
    {
        var deviceArg = string.IsNullOrWhiteSpace(deviceName) ? "default" : Quote(deviceName);
        return useLoopbackFlag
            ? $"-f wasapi -loopback 1 -i {deviceArg}"
            : $"-f wasapi -i {deviceArg}";
    }

    private static string BuildArgs(
        CaptureSettings settings,
        string outputPath,
        int seconds,
        string? audioInput,
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
        if (!string.IsNullOrEmpty(audioInput))
        {
            argsBuilder.Append($"{audioInput} ");
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
            if (stderr.Contains("Unrecognized option 'loopback'", StringComparison.OrdinalIgnoreCase))
            {
                _wasapiSupportsLoopbackFlag = false;
            }

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

    private static string Quote(string value) =>
        value.Contains('"') ? $"\"{value.Replace("\"", "\\\"")}\"" :
        value.Contains(' ') ? $"\"{value}\"" : value;

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
