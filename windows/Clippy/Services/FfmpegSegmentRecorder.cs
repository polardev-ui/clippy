using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Clippy.Models;

namespace Clippy.Services;

/// <summary>
/// Keeps a single long-lived FFmpeg process capturing the screen into a rolling set of
/// 5-second segments.
///
/// One process rather than one-per-segment matters for correctness, not just efficiency:
/// restarting FFmpeg for every segment dropped roughly a second of footage to process
/// startup each time, so the "continuous" buffer was full of holes and a 30-second clip
/// actually spanned closer to 40 seconds of wall time.
///
/// Segments are MPEG-TS rather than MP4 because TS has no trailing index — a segment that
/// FFmpeg is still writing is already decodable. That is what lets a clip include the
/// moment the user actually pressed the key instead of ending up to 5 seconds earlier.
/// </summary>
public sealed class FfmpegSegmentRecorder : IDisposable
{
    public const double SegmentSeconds = 5;
    private const string SegmentPrefix = "seg_";
    private const string SegmentExtension = ".ts";

    private static readonly Regex SegmentPattern =
        new($@"^{SegmentPrefix}(\d+)\{SegmentExtension}$", RegexOptions.Compiled);

    private readonly object _lock = new();

    private Process? _process;
    private AudioMixPipe? _audio;
    private CancellationTokenSource? _watchCts;
    private Task? _watchTask;
    private string _bufferDirectory = "";
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised once per segment as soon as FFmpeg has moved on to the next one.</summary>
    public event Action<RecordingSegment>? SegmentFinished;

    public bool IsRunning
    {
        get { lock (_lock) { return _process is { HasExited: false }; } }
    }

    public void Start(CaptureSettings settings, string bufferDirectory)
    {
        Stop();

        var ffmpeg = FfmpegLocator.Path;
        if (ffmpeg == null)
        {
            ClippyDebugLog.Instance.Log("Recorder", "FFmpeg not found — cannot start capture");
            return;
        }

        _bufferDirectory = bufferDirectory;
        Directory.CreateDirectory(bufferDirectory);
        ClearBufferDirectory(bufferDirectory);
        _announced.Clear();

        var audio = new AudioMixPipe();
        audio.Start(settings.OutputDeviceId, settings.MicrophoneDeviceId, settings.CaptureMicrophone);

        var arguments = BuildArguments(settings, bufferDirectory, audio.IsActive);
        ClippyDebugLog.Instance.Log("Recorder", $"ffmpeg {arguments}");

        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = audio.IsActive,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }) ?? throw new InvalidOperationException("Failed to start FFmpeg.");
        }
        catch (Exception ex)
        {
            audio.Dispose();
            ClippyDebugLog.Instance.LogError("Recorder", ex, "start capture");
            return;
        }

        lock (_lock)
        {
            _process = process;
            _audio = audio;
        }

        // Both pipes must be drained continuously. FFmpeg writes progress to stderr, and a
        // full pipe buffer would block the encoder rather than just losing the messages.
        _ = Task.Run(() => DrainStderr(process));
        _ = Task.Run(() =>
        {
            try { process.StandardOutput.ReadToEnd(); } catch { }
        });

        if (audio.IsActive)
        {
            audio.PumpTo(process.StandardInput.BaseStream);
        }

        ClippyDebugLog.Instance.Log("Recorder",
            $"Audio: system={audio.HasSystemAudio} mic={audio.HasMicrophone}");

        _watchCts = new CancellationTokenSource();
        _watchTask = Task.Run(() => WatchSegments(bufferDirectory, settings, _watchCts.Token));
    }

    public void Stop()
    {
        _watchCts?.Cancel();
        try { _watchTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _watchCts?.Dispose();
        _watchCts = null;
        _watchTask = null;

        Process? process;
        AudioMixPipe? audio;
        lock (_lock)
        {
            process = _process;
            audio = _audio;
            _process = null;
            _audio = null;
        }

        audio?.Dispose();

        if (process == null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                // FFmpeg cannot be asked to quit politely here: its stdin carries the audio
                // stream, so it is not watching for the interactive "q" command, and gdigrab
                // never reaches end-of-input on its own. Killing truncates at most the
                // segment currently being written, which is scratch data either way.
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch { }

        try { process.Dispose(); } catch { }
    }

    /// <summary>
    /// All segments currently on disk, oldest first. The last entry may still be being
    /// written; TS keeps it readable regardless.
    /// </summary>
    public IReadOnlyList<RecordingSegment> SnapshotSegments()
    {
        if (string.IsNullOrEmpty(_bufferDirectory) || !Directory.Exists(_bufferDirectory))
        {
            return Array.Empty<RecordingSegment>();
        }

        var files = EnumerateSegmentFiles(_bufferDirectory);
        return files
            .Select((f, position) => ToSegment(f.Path, f.Index, isComplete: position < files.Count - 1))
            .ToList();
    }

    private void WatchSegments(string bufferDirectory, CaptureSettings settings, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var files = EnumerateSegmentFiles(bufferDirectory);

                // Everything but the highest-numbered file is finished being written.
                for (var i = 0; i < files.Count - 1; i++)
                {
                    var file = files[i];
                    if (!_announced.Add(file.Path))
                    {
                        continue;
                    }

                    SegmentFinished?.Invoke(ToSegment(file.Path, file.Index, isComplete: true));
                }
            }
            catch (Exception ex)
            {
                ClippyDebugLog.Instance.Log("Recorder", $"Segment watch error: {ex.Message}");
            }

            try { Task.Delay(500, token).Wait(token); } catch { break; }
        }
    }

    private static RecordingSegment ToSegment(string path, int index, bool isComplete)
    {
        var info = new FileInfo(path);
        return new RecordingSegment
        {
            Path = path,
            Index = index,
            StartTime = info.CreationTimeUtc,
            Duration = isComplete ? SegmentSeconds : 0,
            IsComplete = isComplete
        };
    }

    private static List<(string Path, int Index)> EnumerateSegmentFiles(string directory)
    {
        var result = new List<(string Path, int Index)>();
        foreach (var path in Directory.EnumerateFiles(directory, $"{SegmentPrefix}*{SegmentExtension}"))
        {
            var match = SegmentPattern.Match(Path.GetFileName(path));
            if (match.Success && int.TryParse(match.Groups[1].Value, out var index))
            {
                result.Add((path, index));
            }
        }

        result.Sort((a, b) => a.Index.CompareTo(b.Index));
        return result;
    }

    private static void ClearBufferDirectory(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, $"{SegmentPrefix}*{SegmentExtension}"))
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static string BuildArguments(CaptureSettings settings, string bufferDirectory, bool withAudio)
    {
        var args = new StringBuilder();
        args.Append("-hide_banner -loglevel warning -y ");

        // gdigrab's -video_size crops the grab region; it does not scale. The desktop is
        // therefore captured at its native size and scaled afterwards, otherwise picking
        // 720p on a 1440p monitor would record the top-left corner instead of the screen.
        args.Append($"-f gdigrab -framerate {settings.FrameRate} -draw_mouse 1 ");
        args.Append($"-offset_x {settings.OffsetX} -offset_y {settings.OffsetY} ");
        args.Append($"-video_size {settings.SourceWidth}x{settings.SourceHeight} -i desktop ");

        if (withAudio)
        {
            args.Append($"-f s16le -ar {AudioMixPipe.SampleRate} -ac {AudioMixPipe.Channels} -i pipe:0 ");
        }

        if (settings.SourceWidth != settings.Width || settings.SourceHeight != settings.Height)
        {
            args.Append($"-vf scale={settings.Width}:{settings.Height}:flags=fast_bilinear ");
        }

        args.Append("-c:v libx264 -preset ultrafast -tune zerolatency ");
        args.Append($"-b:v {settings.VideoBitrate} -pix_fmt yuv420p ");

        // A keyframe every segment length lets the segment muxer cut on exact boundaries.
        args.Append($"-g {settings.FrameRate * (int)SegmentSeconds} -keyint_min {settings.FrameRate} -sc_threshold 0 ");

        args.Append(withAudio ? "-c:a aac -b:a 192k -ar 48000 " : "-an ");

        args.Append($"-f segment -segment_time {(int)SegmentSeconds} -segment_format mpegts ");
        args.Append("-reset_timestamps 1 ");
        args.Append($"\"{Path.Combine(bufferDirectory, $"{SegmentPrefix}%05d{SegmentExtension}")}\"");

        return args.ToString();
    }

    private static void DrainStderr(Process process)
    {
        try
        {
            string? line;
            while ((line = process.StandardError.ReadLine()) != null)
            {
                if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Invalid", StringComparison.OrdinalIgnoreCase))
                {
                    ClippyDebugLog.Instance.Log("Recorder", $"FFmpeg: {line.Trim()}");
                }
            }
        }
        catch { }
    }

    public void Dispose() => Stop();
}
