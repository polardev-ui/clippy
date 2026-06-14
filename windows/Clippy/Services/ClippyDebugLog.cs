using System.Collections.ObjectModel;
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

    public ObservableCollection<DebugLogEntry> Entries { get; } = new();

    public void Log(string category, string message)
    {
        var entry = new DebugLogEntry
        {
            Timestamp = DateTime.Now,
            Category = category,
            Message = message
        };

        Entries.Insert(0, entry);
        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }

        System.Diagnostics.Debug.WriteLine(entry.Formatted);
    }

    public void LogError(string category, Exception error, string context = "")
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(context)) parts.Add(context);
        parts.Add($"{error.GetType().Name}: {error.Message}");
        Log(category, string.Join(" — ", parts));
    }

    public string ExportText =>
        string.Join(Environment.NewLine, Entries.Select(e => e.Formatted));

    public void Clear() => Entries.Clear();
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
