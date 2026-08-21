using System.Text;
using Clippy.Models;

namespace Clippy.Services;

public sealed class DebugLogEntry
{
    public DateTime Timestamp { get; init; }
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";

    public string Formatted =>
        $"[{Timestamp:HH:mm:ss.fff}] [{Category}] {Message}";
}

public sealed class ClippyDebugLog
{
    private const int MaxEntries = 250;
    private static ClippyDebugLog? _instance;
    public static ClippyDebugLog Instance => _instance ??= new ClippyDebugLog();

    // A plain guarded list rather than an ObservableCollection: entries arrive from the
    // capture, audio and voice threads, while the settings panel walks the list on the UI
    // thread. Handing out snapshots removes the thread affinity problem outright.
    private readonly List<DebugLogEntry> _entries = new();
    private readonly object _gate = new();

    /// <summary>Newest first.</summary>
    public IReadOnlyList<DebugLogEntry> Entries
    {
        get { lock (_gate) { return _entries.ToArray(); } }
    }

    public void Log(string category, string message)
    {
        var entry = new DebugLogEntry
        {
            Timestamp = DateTime.Now,
            Category = category,
            Message = message
        };

        lock (_gate)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            }
        }

        System.Diagnostics.Debug.WriteLine(entry.Formatted);
        Changed?.Invoke();
    }

    public event Action? Changed;

    public void LogError(string category, Exception error, string context = "")
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(context)) parts.Add(context);
        parts.Add($"{error.GetType().Name}: {error.Message}");
        Log(category, string.Join(" — ", parts));
    }

    public string ExportText =>
        string.Join(Environment.NewLine, Entries.Select(e => e.Formatted));

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }

        Changed?.Invoke();
    }
}

public static class RecorderDiagnostics
{
    public static string Snapshot(ScreenRecorder recorder) =>
        string.Join("\n", new[]
        {
            $"capturing={recorder.IsCapturing}",
            $"bufferReady={recorder.IsBufferReady}",
            $"bufferedSeconds={recorder.BufferedSeconds:F1}",
            $"segmentCount={recorder.SegmentCount}",
            $"status={recorder.StatusMessage}",
            recorder.InternalDebugState()
        });
}

public static class VoiceDiagnostics
{
    public static void LogSnapshot(VoiceCommandListener voice)
    {
        ClippyDebugLog.Instance.Log("Voice", "Diagnostics snapshot:");
        foreach (var line in Snapshot(voice).Split('\n'))
        {
            ClippyDebugLog.Instance.Log("Voice", line);
        }
    }

    public static string Snapshot(VoiceCommandListener voice)
    {
        var settings = AppSettings.Instance;
        var lines = new List<string>
        {
            $"voiceCommandsEnabled={settings.VoiceCommandsEnabled}",
            $"isListening={voice.IsListening}",
            $"status={voice.StatusMessage}",
            $"activeMicrophone={voice.ActiveMicrophoneName}",
            $"preferredMicId={(string.IsNullOrEmpty(settings.PreferredMicrophoneId) ? "(system default)" : settings.PreferredMicrophoneId)}"
        };

        if (!string.IsNullOrEmpty(voice.LastHeardPhrase))
        {
            lines.Add($"lastHeard=\"{voice.LastHeardPhrase}\"");
        }

        return string.Join("\n", lines);
    }
}
