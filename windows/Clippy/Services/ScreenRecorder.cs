using Clippy.Models;

namespace Clippy.Services;

public sealed class ScreenRecorder
{
    private static ScreenRecorder? _instance;
    public static ScreenRecorder Instance => _instance ??= new ScreenRecorder();

    private const double MaxBufferDuration = 60;
    private const double BufferReadyMinimum = 3;

    private readonly object _stateLock = new();
    private FfmpegSegmentRecorder? _recorder;
    private CancellationTokenSource? _tickerCts;
    private IReadOnlyList<CaptureDisplay> _displays = Array.Empty<CaptureDisplay>();
    private int _completedSegments;
    private DateTime _captureStartedUtc = DateTime.UtcNow;

    public bool IsCapturing { get; private set; }
    public bool IsClipping { get; private set; }
    public bool IsBufferReady { get; private set; }
    public double BufferedSeconds { get; private set; }
    public int SegmentCount { get; private set; }
    public string StatusMessage { get; private set; } = "Starting capture…";
    public string LastClipDebugSummary { get; private set; } = "";

    public event Action? StateChanged;

    private ScreenRecorder()
    {
    }

    public void RequestScreenCaptureAccess()
    {
        StatusMessage = "Starting screen capture…";
        NotifyStateChanged();
    }

    public async Task StartCaptureAsync()
    {
        if (IsCapturing) return;

        if (FfmpegLocator.Path == null)
        {
            StatusMessage = "Recording engine missing — reinstall Clippy.";
            NotifyStateChanged();
            return;
        }

        _displays = DisplayManager.RefreshDisplays();
        var settings = AppSettings.Instance;
        var display = DisplayManager.DisplayById(settings.PreferredDisplayId, _displays)
                      ?? _displays.First();

        if (!string.IsNullOrEmpty(settings.PreferredAudioOutputId))
        {
            AudioDeviceManager.SetDefaultOutputDevice(settings.PreferredAudioOutputId);
        }

        if (!string.IsNullOrEmpty(settings.PreferredMicrophoneId))
        {
            AudioDeviceManager.SetDefaultInputDevice(settings.PreferredMicrophoneId);
        }

        _recorder?.Dispose();
        _recorder = new FfmpegSegmentRecorder();
        _recorder.SegmentFinished += OnSegmentFinished;

        var capture = CaptureSettings.FromAppSettings(display);

        lock (_stateLock)
        {
            _completedSegments = 0;
            BufferedSeconds = 0;
            SegmentCount = 0;
            IsBufferReady = false;
            _captureStartedUtc = DateTime.UtcNow;
        }

        _recorder.Start(capture, ClipStorage.BufferDirectory);

        IsCapturing = true;
        StatusMessage = $"Buffering {display.Label}…";
        StartTicker();

        ClippyDebugLog.Instance.Log("Recorder",
            $"Capture {capture.SourceWidth}x{capture.SourceHeight} → {capture.Width}x{capture.Height} @ {capture.FrameRate}fps");

        NotifyStateChanged();
        await Task.CompletedTask;
    }

    public async Task RestartCaptureAsync()
    {
        await StopCaptureAsync();
        await StartCaptureAsync();
    }

    public async Task StopCaptureAsync()
    {
        _tickerCts?.Cancel();
        _recorder?.Stop();
        IsCapturing = false;
        StatusMessage = "Capture stopped";
        NotifyStateChanged();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Exports the trailing <paramref name="maxDuration"/> seconds of the buffer. Capture
    /// keeps running throughout — stopping it would punch a hole in the buffer for however
    /// long the export took, and the next clip would be missing that stretch.
    /// </summary>
    public async Task<ClipExportResult> CreateClipAsync(double maxDuration)
    {
        IsClipping = true;
        NotifyStateChanged();

        try
        {
            ClippyDebugLog.Instance.Log("Recorder", "--- clip start ---");

            var recorder = _recorder
                ?? throw new InvalidOperationException(
                    "Capture is not running — open Settings → Debug Log for details.");

            // Snapshot to a private directory first. The rolling buffer keeps being written
            // and pruned during the export, so exporting straight from it would race.
            var staging = Path.Combine(ClipStorage.BufferDirectory, $"clip_{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);

            try
            {
                var staged = StageSegments(recorder, maxDuration, staging);
                if (staged.Count == 0)
                {
                    throw new InvalidOperationException(
                        "No recording buffer available yet — wait a few seconds for the buffer to fill. Open Settings → Debug Log for details.");
                }

                var exportPath = Path.Combine(ClipStorage.BufferDirectory, $"export_{Guid.NewGuid():N}.mov");
                var duration = await ClipExporter.ExportAsync(staged, maxDuration, exportPath);

                LastClipDebugSummary =
                    $"Clip OK — {duration:F1}s (target {(int)maxDuration}s) from {staged.Count} segment(s)";
                ClippyDebugLog.Instance.Log("Recorder", LastClipDebugSummary);

                return new ClipExportResult { Path = exportPath, Duration = duration };
            }
            finally
            {
                try { Directory.Delete(staging, recursive: true); } catch { }
            }
        }
        finally
        {
            IsClipping = false;
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Copies the newest segments covering <paramref name="maxDuration"/> into
    /// <paramref name="staging"/>, including the segment FFmpeg is still writing.
    /// </summary>
    private static List<RecordingSegment> StageSegments(
        FfmpegSegmentRecorder recorder,
        double maxDuration,
        string staging)
    {
        var available = recorder.SnapshotSegments()
            .Where(s => File.Exists(s.Path) && new FileInfo(s.Path).Length > 500)
            .ToList();

        if (available.Count == 0)
        {
            return new List<RecordingSegment>();
        }

        // Walk back from the newest until there is enough footage. The in-progress segment
        // has unknown length, so assume a full one when budgeting; the exporter trims the
        // result to size anyway.
        var selected = new List<RecordingSegment>();
        var covered = 0.0;
        for (var i = available.Count - 1; i >= 0; i--)
        {
            selected.Insert(0, available[i]);

            // The in-progress segment contributes an unknown amount, so it is budgeted as
            // nothing. Erring towards staging one segment too many costs a file copy; erring
            // the other way would hand back a clip shorter than the user asked for.
            covered += available[i].IsComplete ? available[i].Duration : 0;

            if (covered >= maxDuration)
            {
                break;
            }
        }

        var staged = new List<RecordingSegment>();
        foreach (var segment in selected)
        {
            var destination = Path.Combine(staging, Path.GetFileName(segment.Path));
            try
            {
                CopyLive(segment.Path, destination);
            }
            catch (Exception ex)
            {
                ClippyDebugLog.Instance.Log("Recorder",
                    $"Skipping segment {Path.GetFileName(segment.Path)}: {ex.Message}");
                continue;
            }

            if (new FileInfo(destination).Length > 500)
            {
                staged.Add(new RecordingSegment
                {
                    Path = destination,
                    Index = segment.Index,
                    StartTime = segment.StartTime,
                    Duration = segment.Duration,
                    IsComplete = segment.IsComplete
                });
            }
        }

        return staged;
    }

    /// <summary>Copies a file FFmpeg still holds open for writing.</summary>
    private static void CopyLive(string source, string destination)
    {
        using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    public string InternalDebugState()
    {
        var onDisk = _recorder?.SnapshotSegments().Count ?? 0;
        return $"bufferDir={ClipStorage.BufferDirectory} | segmentsOnDisk={onDisk} completed={_completedSegments}";
    }

    private void OnSegmentFinished(RecordingSegment segment)
    {
        lock (_stateLock)
        {
            _completedSegments++;
        }

        PruneOldSegments();
        UpdateBufferState();

        ClippyDebugLog.Instance.Log("Recorder",
            $"Segment finished {Path.GetFileName(segment.Path)} dur={segment.Duration:F1}s");
        NotifyStateChanged();
    }

    /// <summary>
    /// Recomputes buffered length from what is actually on disk, counting the in-progress
    /// segment by elapsed time so the readout climbs smoothly instead of in 5s steps.
    /// </summary>
    private void UpdateBufferState()
    {
        var segments = _recorder?.SnapshotSegments() ?? Array.Empty<RecordingSegment>();
        var complete = segments.Count(s => s.IsComplete);

        // Finished segments are a known length; the one still being written is counted by
        // how long capture has been running, so the readout climbs smoothly rather than
        // jumping 5 seconds at a time.
        var buffered = complete * FfmpegSegmentRecorder.SegmentSeconds;
        if (segments.Count > complete)
        {
            var elapsed = (DateTime.UtcNow - _captureStartedUtc).TotalSeconds;
            buffered = Math.Min(elapsed, buffered + FfmpegSegmentRecorder.SegmentSeconds);
        }

        buffered = Math.Min(MaxBufferDuration, buffered);

        lock (_stateLock)
        {
            BufferedSeconds = Math.Max(0, buffered);
            SegmentCount = segments.Count;
            IsBufferReady = BufferedSeconds >= BufferReadyMinimum;
            if (IsCapturing)
            {
                StatusMessage = IsBufferReady
                    ? $"Ready · {BufferedSeconds:F0}s buffered"
                    : $"Buffering… {BufferedSeconds:F0}s";
            }
        }
    }

    private void PruneOldSegments()
    {
        var segments = _recorder?.SnapshotSegments() ?? Array.Empty<RecordingSegment>();

        // Keep one extra segment beyond the window so a clip requesting the full 60s can
        // still be assembled from whole segments.
        var keep = (int)Math.Ceiling(MaxBufferDuration / FfmpegSegmentRecorder.SegmentSeconds) + 1;
        for (var i = 0; i < segments.Count - keep; i++)
        {
            try
            {
                File.Delete(segments[i].Path);
                ClippyDebugLog.Instance.Log("Recorder",
                    $"Pruning segment {Path.GetFileName(segments[i].Path)}");
            }
            catch { }
        }
    }

    private void StartTicker()
    {
        _tickerCts?.Cancel();
        _tickerCts = new CancellationTokenSource();
        var token = _tickerCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(1000, token); } catch { break; }
                UpdateBufferState();
                NotifyStateChanged();
            }
        }, token);
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();
}
