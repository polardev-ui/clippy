using Clippy.Models;
using Windows.Media.SpeechRecognition;

namespace Clippy.Services;

public sealed class VoiceCommandListener
{
    private static VoiceCommandListener? _instance;
    public static VoiceCommandListener Instance => _instance ??= new VoiceCommandListener();

    private SpeechContinuousRecognitionSession? _session;
    private SpeechRecognizer? _recognizer;
    private bool _isListening;
    private bool _hasTriggeredThisUtterance;
    private string _lastLoggedPhrase = "";

    public bool IsListening => _isListening;
    public bool OnboardingMode { get; set; }
    public string StatusMessage { get; private set; } = "Voice idle";
    public string? LastHeardPhrase { get; private set; }
    public string? LastVoiceError { get; private set; }
    public string ActiveMicrophoneName { get; private set; } = "System Default";

    public Action? OnClipCommand { get; set; }
    public Action? OnOnboardingClipCommand { get; set; }

    public event Action? StateChanged;

    private VoiceCommandListener()
    {
    }

    public void RefreshMicrophone()
    {
        var settings = AppSettings.Instance;
        ActiveMicrophoneName = string.IsNullOrEmpty(settings.PreferredMicrophoneId)
            ? "System Default"
            : AudioDeviceManager.ResolvedDeviceName(
                settings.PreferredMicrophoneId, NAudio.CoreAudioApi.DataFlow.Capture);
        NotifyStateChanged();
    }

    public async Task PrepareAndStartAsync()
    {
        if (!AppSettings.Instance.VoiceCommandsEnabled)
        {
            StatusMessage = "Voice commands off";
            await StopListeningAsync();
            NotifyStateChanged();
            return;
        }

        try
        {
            await StopListeningAsync();

            _recognizer = new SpeechRecognizer();
            var grammar = new SpeechRecognitionListConstraint(new[]
            {
                "Clippy",
                "clip that",
                "clip this",
                "clip it",
                "do your thing"
            });
            await _recognizer.CompileConstraintsAsync();

            _session = _recognizer.ContinuousRecognitionSession;
            _session.ResultGenerated += OnResultGenerated;
            _session.Completed += OnCompleted;

            await _session.StartAsync();
            _isListening = true;
            StatusMessage = "Listening for \"Clippy, clip that\"…";
            ClippyDebugLog.Instance.Log("Voice", "Starting recognition task");
        }
        catch (Exception ex)
        {
            LastVoiceError = ex.Message;
            StatusMessage = "Voice error — retrying…";
            ClippyDebugLog.Instance.LogError("Voice", ex, "prepareAndStart");
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                await PrepareAndStartAsync();
            });
        }

        NotifyStateChanged();
    }

    public async Task StopListeningAsync()
    {
        _isListening = false;
        if (_session != null)
        {
            _session.ResultGenerated -= OnResultGenerated;
            _session.Completed -= OnCompleted;
            try { await _session.StopAsync(); } catch { }
            _session = null;
        }

        _recognizer?.Dispose();
        _recognizer = null;
        StatusMessage = "Voice idle";
        NotifyStateChanged();
    }

    private void OnCompleted(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionCompletedEventArgs args)
    {
        if (AppSettings.Instance.VoiceCommandsEnabled)
        {
            _ = PrepareAndStartAsync();
        }
    }

    private void OnResultGenerated(
        SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        if (args.Result.Confidence == SpeechRecognitionConfidence.Rejected) return;

        var text = args.Result.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text)) return;

        LastHeardPhrase = text;
        if (_lastLoggedPhrase != text)
        {
            _lastLoggedPhrase = text;
            ClippyDebugLog.Instance.Log("Voice", $"Heard: \"{text}\"");
        }

        if (args.Result.Confidence == SpeechRecognitionConfidence.High ||
            MatchesTrigger(text))
        {
            if (_hasTriggeredThisUtterance) return;
            _hasTriggeredThisUtterance = true;
            StatusMessage = "Heard clip command!";

            if (OnboardingMode)
            {
                OnOnboardingClipCommand?.Invoke();
            }
            else
            {
                OnClipCommand?.Invoke();
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                _hasTriggeredThisUtterance = false;
                await StopListeningAsync();
                await PrepareAndStartAsync();
            });
        }

        NotifyStateChanged();
    }

    public static bool MatchesTrigger(string text)
    {
        var normalized = text.ToLowerInvariant()
            .Replace(",", " ")
            .Replace(".", " ")
            .Replace("'", "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var joined = string.Join(' ', normalized);

        string[] explicitPhrases =
        {
            "do your thing",
            "do ya thing",
            "clip that",
            "clip this",
            "clip it",
            "clippy clip",
            "clippy do"
        };

        if (explicitPhrases.Any(joined.Contains))
        {
            return true;
        }

        var hasWake = joined.Contains("clipp") || joined.Contains("clippy") || joined.Contains("clipty");
        var hasClipIntent = joined.Contains("clip") || joined.Contains("thing");
        return hasWake && hasClipIntent;
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();
}
