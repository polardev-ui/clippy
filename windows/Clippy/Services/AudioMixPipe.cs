using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Clippy.Services;

/// <summary>
/// Captures system (loopback) and microphone audio with WASAPI, mixes them, and writes a
/// steady 48 kHz stereo s16le stream into a pipe that FFmpeg reads as a live input.
///
/// Doing the capture in managed code rather than through FFmpeg's own wasapi/dshow demuxers
/// means any FFmpeg build works, device selection goes by endpoint ID instead of a display
/// name that has to survive being escaped into a command line, and — most importantly — the
/// stream is paced by a clock here, so silence still advances it. WASAPI loopback delivers
/// no packets at all while nothing is playing; without generated silence the audio track
/// would drift out of sync with video by however long the machine was quiet.
/// </summary>
public sealed class AudioMixPipe : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private const int FramesPerTick = SampleRate / 50; // 20 ms

    private static readonly WaveFormat MixFormat =
        WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    private readonly List<IDisposable> _disposables = new();
    private readonly MixingSampleProvider _mixer = new(MixFormat) { ReadFully = true };
    private readonly CancellationTokenSource _cts = new();

    private Task? _pumpTask;

    public bool HasSystemAudio { get; private set; }
    public bool HasMicrophone { get; private set; }
    public bool IsActive => HasSystemAudio || HasMicrophone;

    /// <summary>Starts the capture devices. Never throws; missing devices are simply absent.</summary>
    public void Start(string? outputDeviceId, string? microphoneDeviceId, bool captureMicrophone)
    {
        HasSystemAudio = TryAddCapture(
            () => CreateLoopbackCapture(outputDeviceId), "system audio");

        if (captureMicrophone)
        {
            HasMicrophone = TryAddCapture(
                () => CreateMicrophoneCapture(microphoneDeviceId), "microphone");
        }
    }

    /// <summary>Pumps mixed audio into <paramref name="destination"/> until disposed.</summary>
    public void PumpTo(Stream destination)
    {
        _pumpTask = Task.Run(() => PumpLoop(destination, _cts.Token));
    }

    private void PumpLoop(Stream destination, CancellationToken token)
    {
        var floats = new float[FramesPerTick * Channels];
        var bytes = new byte[floats.Length * sizeof(short)];
        var clock = Stopwatch.StartNew();
        long framesWritten = 0;

        while (!token.IsCancellationRequested)
        {
            // Pace against a wall clock rather than sleeping a fixed interval, so timer
            // jitter cannot accumulate into audio/video drift over a long session.
            var due = (long)(clock.Elapsed.TotalSeconds * SampleRate);
            if (framesWritten > due)
            {
                try { Task.Delay(5, token).Wait(token); } catch { break; }
                continue;
            }

            var read = _mixer.Read(floats, 0, floats.Length);
            for (var i = read; i < floats.Length; i++)
            {
                floats[i] = 0f;
            }

            for (var i = 0; i < floats.Length; i++)
            {
                var clamped = Math.Clamp(floats[i], -1f, 1f);
                var sample = (short)(clamped * short.MaxValue);
                bytes[i * 2] = (byte)(sample & 0xFF);
                bytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            try
            {
                destination.Write(bytes, 0, bytes.Length);
                destination.Flush();
            }
            catch
            {
                // FFmpeg exited or the pipe closed — nothing left to feed.
                break;
            }

            framesWritten += FramesPerTick;
        }
    }

    private bool TryAddCapture(Func<WasapiCapture> factory, string label)
    {
        try
        {
            var capture = factory();
            var buffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                // ReadFully makes the provider emit silence when the device is quiet,
                // which is what keeps the mix advancing in real time.
                ReadFully = true,
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2)
            };

            capture.DataAvailable += (_, e) =>
            {
                try { buffer.AddSamples(e.Buffer, 0, e.BytesRecorded); } catch { }
            };

            _mixer.AddMixerInput(ToMixFormat(buffer));
            capture.StartRecording();

            _disposables.Add(capture);
            return true;
        }
        catch (Exception ex)
        {
            ClippyDebugLog.Instance.Log("Recorder", $"Could not capture {label}: {ex.Message}");
            return false;
        }
    }

    private static ISampleProvider ToMixFormat(BufferedWaveProvider buffer)
    {
        ISampleProvider provider = buffer.ToSampleProvider();

        if (provider.WaveFormat.Channels > Channels)
        {
            // Keep the front-left/right pair; NAudio cannot mix down surround for us.
            provider = new MultiplexingSampleProvider(new[] { provider }, Channels);
        }
        else if (provider.WaveFormat.Channels == 1)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }

        if (provider.WaveFormat.SampleRate != SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, SampleRate);
        }

        return provider;
    }

    private static WasapiCapture CreateLoopbackCapture(string? outputDeviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrEmpty(outputDeviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(outputDeviceId);
        return new WasapiLoopbackCapture(device);
    }

    private static WasapiCapture CreateMicrophoneCapture(string? microphoneDeviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrEmpty(microphoneDeviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : enumerator.GetDevice(microphoneDeviceId);
        return new WasapiCapture(device);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _pumpTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }

        foreach (var disposable in _disposables)
        {
            try
            {
                if (disposable is WasapiCapture capture)
                {
                    capture.StopRecording();
                }

                disposable.Dispose();
            }
            catch { }
        }

        _disposables.Clear();
        _cts.Dispose();
    }
}
