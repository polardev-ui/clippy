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
    private bool _loggedAudioUnavailable;
    private bool _useNaudioSystemAudio = true;

    public event Action<RecordingSegment>? SegmentFinished;

    public bool IsRunning => _process is { HasExited: false };

    public void Start(CaptureSettings settings, string bufferDirectory, bool preserveState = false)
    {
        Stop();

        if (!preserveState)
        {
            _loggedAudioUnavailable = false;
            _useNaudioSystemAudio = !FfmpegCapabilities.SupportsWasapi;
        }

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
                var result = await RecordSegmentAsync(settings, segmentPath, 5, token);
                if (result.Recorded && File.Exists(segmentPath) && new FileInfo(segmentPath).Length > 500)
                {
                    if (result.HadMic && !result.UsedMic)
                    {
                        ClippyDebugLog.Instance.Log("Recorder",
                            "Recording without microphone — mic may be in use by voice recognition");
                    }

                    if (!result.UsedMic && !result.UsedSystemAudio && !_loggedAudioUnavailable)
                    {
                        _loggedAudioUnavailable = true;
                        ClippyDebugLog.Instance.Log("Recorder",
                            "No audio in buffer segments — check speakers and mic in Settings");
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

    private async Task<SegmentCaptureResult> RecordSegmentAsync(
        CaptureSettings settings,
        string outputPath,
        int seconds,
        CancellationToken token)
    {
        PrepareOutputFile(outputPath);

        if (_useNaudioSystemAudio || !FfmpegCapabilities.SupportsWasapi)
        {
            return await RecordWithNaudioAsync(settings, outputPath, seconds, token);
        }

        var ffmpegResult = await TryFfmpegWasapiSegmentAsync(settings, outputPath, seconds, token);
        if (ffmpegResult.Recorded)
        {
            return ffmpegResult;
        }

        _useNaudioSystemAudio = true;
        return await RecordWithNaudioAsync(settings, outputPath, seconds, token);
    }

    private async Task<SegmentCaptureResult> RecordWithNaudioAsync(
        CaptureSettings settings,
        string outputPath,
        int seconds,
        CancellationToken token)
    {
        var hadMic = !string.IsNullOrEmpty(settings.MicrophoneDeviceId) ||
                     !string.IsNullOrEmpty(settings.MicrophoneDeviceName);
        var videoTemp = Path.Combine(Path.GetTempPath(), $"clippy_vid_{Guid.NewGuid():N}.mp4");

        try
        {
            var videoTask = RecordVideoOnlyAsync(settings, videoTemp, seconds, token);
            var loopTask = NaudioSegmentAudio.RecordLoopbackWavAsync(settings.OutputDeviceId, seconds, token);
            var micTask = hadMic
                ? NaudioSegmentAudio.RecordMicWavAsync(settings.MicrophoneDeviceId, seconds, token)
                : Task.FromResult<string?>(null);

            await Task.WhenAll(videoTask, loopTask, micTask);

            if (!videoTask.Result || !File.Exists(videoTemp) || new FileInfo(videoTemp).Length < 500)
            {
                return new SegmentCaptureResult(false, hadMic, false, false);
            }

            var loopWav = loopTask.Result;
            var micWav = micTask.Result;
            var usedSystem = !string.IsNullOrEmpty(loopWav);
            var usedMic = !string.IsNullOrEmpty(micWav);

            if (usedSystem || usedMic)
            {
                if (await MuxSegmentAsync(videoTemp, loopWav, micWav, outputPath, seconds, token))
                {
                    return new SegmentCaptureResult(true, hadMic, usedMic, usedSystem);
                }
            }

            File.Move(videoTemp, outputPath, overwrite: true);
            return new SegmentCaptureResult(true, hadMic, false, false);
        }
        finally
        {
            try { if (File.Exists(videoTemp)) File.Delete(videoTemp); } catch { }
        }
    }

    private async Task<SegmentCaptureResult> TryFfmpegWasapiSegmentAsync(
        CaptureSettings settings,
        string outputPath,
        int seconds,
        CancellationToken token)
    {
        var hadMic = !string.IsNullOrEmpty(settings.MicrophoneDeviceName);
        foreach (var systemInput in BuildWasapiSystemInputs(settings.OutputDeviceName))
        {
            PrepareOutputFile(outputPath);
            var plan = new AudioCapturePlan(systemInput, UseMic: false);
            var args = BuildArgs(settings, outputPath, seconds, plan);
            var (exitCode, _) = await RunFfmpegAsync(args, token);
            if (exitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 500)
            {
                return new SegmentCaptureResult(true, hadMic, false, true);
            }

            if (!FfmpegCapabilities.SupportsWasapi)
            {
                break;
            }
        }

        return new SegmentCaptureResult(false, hadMic, false, false);
    }

    private async Task<bool> RecordVideoOnlyAsync(
        CaptureSettings settings,
        string outputPath,
        int seconds,
        CancellationToken token)
    {
        PrepareOutputFile(outputPath);
        var args = BuildArgs(settings, outputPath, seconds, new AudioCapturePlan(null, false));
        var (exitCode, _) = await RunFfmpegAsync(args, token);
        return exitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 500;
    }

    private static async Task<bool> MuxSegmentAsync(
        string videoPath,
        string? loopWav,
        string? micWav,
        string outputPath,
        int seconds,
        CancellationToken token)
    {
        PrepareOutputFile(outputPath);

        string args;
        if (!string.IsNullOrEmpty(loopWav) && !string.IsNullOrEmpty(micWav))
        {
            args = $"-y -i \"{videoPath}\" -i \"{loopWav}\" -i \"{micWav}\" " +
                   "-filter_complex \"[1:a][2:a]amix=inputs=2:duration=first:dropout_transition=0[aout]\" " +
                   $"-map 0:v -map \"[aout]\" -c:v copy -c:a aac -b:a 192k -t {seconds} \"{outputPath}\"";
        }
        else if (!string.IsNullOrEmpty(loopWav))
        {
            args = $"-y -i \"{videoPath}\" -i \"{loopWav}\" -map 0:v -map 1:a -c:v copy -c:a aac -b:a 192k " +
                   $"-shortest -t {seconds} \"{outputPath}\"";
        }
        else if (!string.IsNullOrEmpty(micWav))
        {
            args = $"-y -i \"{videoPath}\" -i \"{micWav}\" -map 0:v -map 1:a -c:v copy -c:a aac -b:a 192k " +
                   $"-shortest -t {seconds} \"{outputPath}\"";
        }
        else
        {
            return false;
        }

        try
        {
            await ClipExporter.RunFfmpegAsync(args);
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 500;
        }
        catch (Exception ex)
        {
            ClippyDebugLog.Instance.Log("Recorder", $"Mux failed: {ex.Message}");
            return false;
        }
        finally
        {
            try { if (!string.IsNullOrEmpty(loopWav)) File.Delete(loopWav); } catch { }
            try { if (!string.IsNullOrEmpty(micWav)) File.Delete(micWav); } catch { }
        }
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

    private readonly record struct AudioCapturePlan(string? SystemInput, bool UseMic, string? MicDeviceName = null);

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
    public string? MicrophoneDeviceId { get; init; }
    public string? OutputDeviceName { get; init; }
    public string? OutputDeviceId { get; init; }

    public (int Width, int Height) Dimensions => (Width, Height);

    public static CaptureSettings FromAppSettings(CaptureDisplay display)
    {
        var settings = AppSettings.Instance;
        var (width, height) = CaptureResolutionExtensions.DimensionsFor(
            settings.CaptureResolution, display.Width, display.Height);

        string? micName = null;
        string? micId = null;
        if (!string.IsNullOrEmpty(settings.PreferredMicrophoneId))
        {
            micId = settings.PreferredMicrophoneId;
            var resolved = AudioDeviceManager.ResolvedDeviceName(micId, NAudio.CoreAudioApi.DataFlow.Capture);
            if (resolved is not "System Default" and not "Unknown Device")
            {
                micName = DshowDeviceResolver.ResolveMicrophoneName(resolved);
            }
        }

        string? outputName = null;
        string? outputId = null;
        if (!string.IsNullOrEmpty(settings.PreferredAudioOutputId))
        {
            outputId = settings.PreferredAudioOutputId;
            outputName = AudioDeviceManager.ResolvedDeviceName(outputId, NAudio.CoreAudioApi.DataFlow.Render);
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
            MicrophoneDeviceId = micId,
            OutputDeviceName = outputName is "System Default" or "Unknown Device" ? null : outputName,
            OutputDeviceId = outputId
        };
    }
}
