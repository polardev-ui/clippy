using System.Collections.ObjectModel;
using Clippy.Models;

namespace Clippy.Services;

public sealed class ScreenRecorder
{
    private static ScreenRecorder? _instance;
    public static ScreenRecorder Instance => _instance ??= new ScreenRecorder();

    private const double SegmentDuration = 5;
    private const double MaxBufferDuration = 60;
    private const double BufferReadyMinimum = 3;

    private readonly List<RecordingSegment> _segments = new();
    private readonly object _segmentLock = new();
    private FfmpegSegmentRecorder? _recorder;
    private CancellationTokenSource? _tickerCts;
    private IReadOnlyList<CaptureDisplay> _displays = Array.Empty<CaptureDisplay>();

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
        _recorder.Start(capture, ClipStorage.BufferDirectory);

        IsCapturing = true;
        StatusMessage = $"Buffering {display.Label}…";
        StartTicker();

        ClippyDebugLog.Instance.Log("Recorder",
            $"Capture {capture.Width}x{capture.Height} @ {capture.FrameRate}fps | output={settings.PreferredAudioOutputId} | mic={settings.PreferredMicrophoneId}");

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

    public async Task<ClipExportResult> CreateClipAsync(double maxDuration)
    {
        IsClipping = true;
        NotifyStateChanged();

        try
        {
            ClippyDebugLog.Instance.Log("Recorder", "--- clip start ---");
            ClippyDebugLog.Instance.Log("Recorder", RecorderDiagnostics.Snapshot(this).Replace('\n', '|'));

            _recorder?.Stop();
            await Task.Delay(200);

            List<RecordingSegment> sourceSegments;
            lock (_segmentLock)
            {
                sourceSegments = _segments
                    .Where(s => ClipExporter.HasReadableVideoSync(s.Path))
                    .OrderBy(s => s.StartTime)
                    .ToList();
            }

            if (sourceSegments.Count == 0)
            {
                throw new InvalidOperationException(
                    "No recording buffer available yet — wait a few seconds for the buffer to fill. Open Settings → Debug Log for details.");
            }

            var available = sourceSegments.Sum(s => s.Duration);
            var selected = new List<RecordingSegment>();
            var needed = maxDuration;
            for (var i = sourceSegments.Count - 1; i >= 0 && needed > 0; i--)
            {
                selected.Insert(0, sourceSegments[i]);
                needed -= sourceSegments[i].Duration;
            }

            var exportPath = Path.Combine(ClipStorage.BufferDirectory, $"export_{Guid.NewGuid():N}.mov");
            await ClipExporter.ExportAsync(selected, maxDuration, exportPath);

            LastClipDebugSummary =
                $"Clip OK — {Math.Min(maxDuration, available):F1}s (target {(int)maxDuration}s) from {selected.Count} segment(s)";
            ClippyDebugLog.Instance.Log("Recorder", LastClipDebugSummary);

            return new ClipExportResult
            {
                Path = exportPath,
                Duration = Math.Min(maxDuration, available)
            };
        }
        finally
        {
            IsClipping = false;
            if (IsCapturing)
            {
                var display = DisplayManager.DisplayById(AppSettings.Instance.PreferredDisplayId, _displays)
                    ?? _displays.FirstOrDefault();
                if (display != null)
                {
                    var capture = CaptureSettings.FromAppSettings(display);
                    _recorder?.Start(capture, ClipStorage.BufferDirectory, preserveState: true);
                }
            }

            NotifyStateChanged();
        }
    }

    public string InternalDebugState()
    {
        lock (_segmentLock)
        {
            var valid = _segments.Count(s => ClipExporter.HasReadableVideoSync(s.Path));
            return
                $"bufferDir={ClipStorage.BufferDirectory} | segmentsInMemory={_segments.Count} validOnDisk={valid}";
        }
    }

    private void OnSegmentFinished(RecordingSegment segment)
    {
        lock (_segmentLock)
        {
            _segments.Add(segment);
            PruneOldSegments();
            BufferedSeconds = _segments.Sum(s => s.Duration);
            SegmentCount = _segments.Count;
            IsBufferReady = BufferedSeconds >= BufferReadyMinimum ||
                            _segments.Any(s => s.Duration >= SegmentDuration - 0.5);
            StatusMessage = IsBufferReady
                ? $"Ready · {BufferedSeconds:F0}s buffered"
                : $"Buffering… {BufferedSeconds:F0}s";
        }

        ClippyDebugLog.Instance.Log("Recorder",
            $"Segment finished {Path.GetFileName(segment.Path)} dur={segment.Duration:F1}s");
        NotifyStateChanged();
    }

    private void PruneOldSegments()
    {
        while (_segments.Count > 0 && _segments.Sum(s => s.Duration) > MaxBufferDuration)
        {
            var oldest = _segments[0];
            _segments.RemoveAt(0);
            try { File.Delete(oldest.Path); } catch { }
            ClippyDebugLog.Instance.Log("Recorder", $"Pruning segment {Path.GetFileName(oldest.Path)}");
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
                await Task.Delay(1000, token);
                NotifyStateChanged();
            }
        }, token);
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();
}
