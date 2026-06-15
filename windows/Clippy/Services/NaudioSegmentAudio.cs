using System.Diagnostics;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Clippy.Services;

public static class FfmpegCapabilities
{
    private static bool _probed;
    private static bool _supportsWasapi;
    private static bool _supportsDshow;
    private static bool _supportsLoopbackFlag;
    private static bool _runtimeWasapiDisabled;
    private static string _versionSummary = "unknown";

    public static bool SupportsWasapi
    {
        get { EnsureProbed(); return _supportsWasapi && !_runtimeWasapiDisabled; }
    }

    public static bool SupportsNaudioLoopback { get; } = true;

    public static bool SupportsDshow
    {
        get { EnsureProbed(); return _supportsDshow; }
    }

    public static bool SupportsLoopbackFlag
    {
        get { EnsureProbed(); return _supportsLoopbackFlag && SupportsWasapi; }
    }

    public static string VersionSummary
    {
        get { EnsureProbed(); return _versionSummary; }
    }

    public static void DisableWasapi()
    {
        EnsureProbed();
        _runtimeWasapiDisabled = true;
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
        var wasapiHelp = RunQuiet(ffmpeg, "-hide_banner -h demuxer=wasapi");
        var wasapiHelpValid = wasapiHelp.Contains("wasapi", StringComparison.OrdinalIgnoreCase) &&
                              !wasapiHelp.Contains("Unknown demuxer", StringComparison.OrdinalIgnoreCase);

        _supportsWasapi = wasapiHelpValid && ProbeWasapiListDevices(ffmpeg);
        _supportsDshow = Regex.IsMatch(formats, @"\bdshow\b", RegexOptions.IgnoreCase) ||
                         ProbeDshowListDevices(ffmpeg);

        _supportsLoopbackFlag = _supportsWasapi &&
                                wasapiHelp.Contains("loopback", StringComparison.OrdinalIgnoreCase);

        ClippyDebugLog.Instance.Log("Recorder",
            $"FFmpeg: {FfmpegLocator.Path} | {_versionSummary}");
        ClippyDebugLog.Instance.Log("Recorder",
            $"Audio capture: ffmpegWasapi={_supportsWasapi} naudioLoopback=True dshow={_supportsDshow} loopbackFlag={_supportsLoopbackFlag}");

        if (!_supportsWasapi)
        {
            ClippyDebugLog.Instance.Log("Recorder",
                "Using NAudio for system audio (FFmpeg WASAPI input unavailable on this system)");
        }
    }

    private static bool ProbeWasapiListDevices(string ffmpeg)
    {
        return RunExitCode(ffmpeg, "-hide_banner -f wasapi -list_devices true -i dummy") == 0;
    }

    private static bool ProbeDshowListDevices(string ffmpeg)
    {
        var output = RunQuiet(ffmpeg, "-hide_banner -list_devices true -f dshow -i dummy");
        return output.Contains("(audio)", StringComparison.OrdinalIgnoreCase);
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

    private static int RunExitCode(string ffmpeg, string arguments)
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
                return -1;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(15));
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}

public static class NaudioSegmentAudio
{
    public static async Task<string?> RecordLoopbackWavAsync(string? outputDeviceId, int seconds, CancellationToken token)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice device;
            if (string.IsNullOrEmpty(outputDeviceId))
            {
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            else
            {
                device = enumerator.GetDevice(outputDeviceId);
            }

            var wavPath = Path.Combine(Path.GetTempPath(), $"clippy_loop_{Guid.NewGuid():N}.wav");
            using var capture = new WasapiLoopbackCapture(device);
            using var writer = new WaveFileWriter(wavPath, capture.WaveFormat);

            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.DataAvailable += (_, e) => writer.Write(e.Buffer, 0, e.BytesRecorded);
            capture.RecordingStopped += (_, _) => stopped.TrySetResult();

            capture.StartRecording();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), token);
            }
            finally
            {
                capture.StopRecording();
            }

            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3), token);

            if (!File.Exists(wavPath) || new FileInfo(wavPath).Length < 500)
            {
                try { File.Delete(wavPath); } catch { }
                return null;
            }

            return wavPath;
        }
        catch (Exception ex)
        {
            ClippyDebugLog.Instance.Log("Recorder", $"NAudio loopback failed: {ex.Message}");
            return null;
        }
    }

    public static async Task<string?> RecordMicWavAsync(string? inputDeviceId, int seconds, CancellationToken token)
    {
        if (VoiceCommandListener.Instance.IsListening)
        {
            return null;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice device;
            if (string.IsNullOrEmpty(inputDeviceId))
            {
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
            else
            {
                device = enumerator.GetDevice(inputDeviceId);
            }

            var wavPath = Path.Combine(Path.GetTempPath(), $"clippy_mic_{Guid.NewGuid():N}.wav");
            using var capture = new WasapiCapture(device);
            using var writer = new WaveFileWriter(wavPath, capture.WaveFormat);

            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.DataAvailable += (_, e) => writer.Write(e.Buffer, 0, e.BytesRecorded);
            capture.RecordingStopped += (_, _) => stopped.TrySetResult();

            capture.StartRecording();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), token);
            }
            finally
            {
                capture.StopRecording();
            }

            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3), token);

            if (!File.Exists(wavPath) || new FileInfo(wavPath).Length < 500)
            {
                try { File.Delete(wavPath); } catch { }
                return null;
            }

            return wavPath;
        }
        catch (Exception ex)
        {
            ClippyDebugLog.Instance.Log("Recorder", $"NAudio mic failed: {ex.Message}");
            return null;
        }
    }
}
