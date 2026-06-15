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
    private AudioCapturePlan? _cachedPlan;
    private bool _loggedAudioUnavailable;

    public event Action<RecordingSegment>? SegmentFinished;

    public bool IsRunning => _process is { HasExited: false };

    public void Start(CaptureSettings settings, string bufferDirectory)
    {
        Stop();
        FfmpegCapabilities.Reset();
        DshowDeviceResolver.InvalidateCache();
        _cachedPlan = null;
        _loggedAudioUnavailable = false;

        _ = FfmpegCapabilities.SupportsWasapi;

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
                var result = await RecordSegmentWithFallbackAsync(settings, segmentPath, 5, token);
                if (result.Recorded && File.Exists(segmentPath) && new FileInfo(segmentPath).Length > 500)
                {
                    if (result.HadMic && !result.UsedMic)
                    {
                        ClippyDebugLog.Instance.Log("Recorder", "Recording without microphone — check mic device in Settings");
                    }

                    if (!result.UsedMic && !result.UsedSystemAudio && !_loggedAudioUnavailable)
                    {
                        _loggedAudioUnavailable = true;
                        var hint = FfmpegLocator.IsBundled && FfmpegCapabilities.SupportsWasapi
                            ? "check audio devices in Settings"
                            : "reinstall Clippy to get the full FFmpeg build with WASAPI, then check audio devices";
                        ClippyDebugLog.Instance.Log("Recorder", $"No audio in buffer segments — {hint}");
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

    private async Task<SegmentCaptureResult> RecordSegmentWithFallbackAsync(
        CaptureSettings settings,
        string outputPath,
        int seconds,
        CancellationToken token)
    {
        var hadMic = !string.IsNullOrEmpty(settings.MicrophoneDeviceName);
        var tried = new HashSet<AudioCapturePlan>();

        while (true)
        {
            var plans = BuildAudioPlans(settings, hadMic);
            if (_cachedPlan is { } cached)
            {
                plans = new[] { cached }.Concat(plans.Where(p => !p.Equals(cached))).ToList();
            }

            var remaining = plans.Where(p => !tried.Contains(p)).ToList();
            if (remaining.Count == 0)
            {
                break;
            }

            foreach (var plan in remaining)
            {
                tried.Add(plan);
                PrepareOutputFile(outputPath);
                var args = BuildArgs(settings, outputPath, seconds, plan);
                var (exitCode, stderr) = await RunFfmpegAsync(args, token);
                if (exitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 500)
                {
                    _cachedPlan = plan;
                    return new SegmentCaptureResult(true, hadMic, plan.UseMic, plan.HasSystemAudio);
                }

                if (!FfmpegCapabilities.SupportsWasapi && plan.HasSystemAudio)
                {
                    break;
                }
            }
        }

        _cachedPlan = null;
        return new SegmentCaptureResult(false, hadMic, false, false);
    }

    private static IReadOnlyList<AudioCapturePlan> BuildAudioPlans(CaptureSettings settings, bool hadMic)
    {
        var plans = new List<AudioCapturePlan>();

        if (FfmpegCapabilities.SupportsWasapi)
        {
            foreach (var systemInput in BuildWasapiSystemInputs(settings.OutputDeviceName))
            {
                if (hadMic && FfmpegCapabilities.SupportsDshow)
                {
                    foreach (var mic in DshowDeviceResolver.MicrophoneCandidates(settings.MicrophoneDeviceName))
                    {
                        plans.Add(new AudioCapturePlan(systemInput, UseMic: true, MicDeviceName: mic));
                    }
                }

                plans.Add(new AudioCapturePlan(systemInput, UseMic: false));
            }
        }

        if (hadMic && FfmpegCapabilities.SupportsDshow)
        {
            foreach (var mic in DshowDeviceResolver.MicrophoneCandidates(settings.MicrophoneDeviceName))
            {
                plans.Add(new AudioCapturePlan(SystemInput: null, UseMic: true, MicDeviceName: mic));
            }
        }

        plans.Add(new AudioCapturePlan(SystemInput: null, UseMic: false));
        return plans;
    }

    private static IReadOnlyList<string> BuildWasapiSystemInputs(string? outputDeviceName)
    {
        var device = string.IsNullOrWhiteSpace(outputDeviceName) ? null : outputDeviceName.Trim();
        return EnumerateWasapiSystemInputs(device).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> EnumerateWasapiSystemInputs(string? device)
    {
        if (FfmpegCapabilities.SupportsLoopbackFlag)
        {
            yield return WasapiInput(device, useLoopbackFlag: true);
            if (device != null)
            {
                yield return WasapiInput($"{device} (loopback)", useLoopbackFlag: true);
            }
        }

        if (device != null)
        {
            yield return WasapiInput(device, useLoopbackFlag: false);
            yield return WasapiInput($"{device} (loopback)", useLoopbackFlag: false);
        }

        yield return WasapiInput(null, useLoopbackFlag: false);
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
        AudioCapturePlan plan)
    {
        var (width, height) = settings.Dimensions;
        var fps = settings.FrameRate;
        var bitrate = settings.VideoBitrate;

        var argsBuilder = new StringBuilder();
        argsBuilder.Append($"-y -f gdigrab -framerate {fps} -draw_mouse 1 ");
        argsBuilder.Append($"-offset_x {settings.OffsetX} -offset_y {settings.OffsetY} ");
        argsBuilder.Append($"-video_size {width}x{height} -i desktop ");

        var inputCount = 1;
        if (!string.IsNullOrEmpty(plan.SystemInput))
        {
            argsBuilder.Append($"{plan.SystemInput} ");
            inputCount++;
        }

        var micName = plan.MicDeviceName ?? settings.MicrophoneDeviceName;
        if (plan.UseMic && !string.IsNullOrEmpty(micName))
        {
            argsBuilder.Append($"-f dshow -i audio=\"{EscapeDshow(micName)}\" ");
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

    private async Task<(int ExitCode, string Stderr)> RunFfmpegAsync(string arguments, CancellationToken token)
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
            FfmpegCapabilities.NoteRuntimeError(stderr);
            ClippyDebugLog.Instance.Log("Recorder", $"FFmpeg exit {process.ExitCode}: {TrimStderr(stderr)}");
        }

        return (process.ExitCode, stderr);
    }

    private static void PrepareOutputFile(string outputPath)
    {
        if (!File.Exists(outputPath))
        {
            return;
        }

        try { File.Delete(outputPath); } catch { }
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

    private readonly record struct AudioCapturePlan(string? SystemInput, bool UseMic, string? MicDeviceName = null)
    {
        public bool HasSystemAudio => !string.IsNullOrEmpty(SystemInput);
    }

    private readonly record struct SegmentCaptureResult(
        bool Recorded,
        bool HadMic,
        bool UsedMic,
        bool UsedSystemAudio);
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
            var resolved = AudioDeviceManager.ResolvedDeviceName(
                settings.PreferredMicrophoneId, NAudio.CoreAudioApi.DataFlow.Capture);
            if (resolved is not "System Default" and not "Unknown Device")
            {
                micName = DshowDeviceResolver.ResolveMicrophoneName(resolved);
            }
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
            MicrophoneDeviceName = micName,
            OutputDeviceName = outputName is "System Default" or "Unknown Device" ? null : outputName
        };
    }
}
