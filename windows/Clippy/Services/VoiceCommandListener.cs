using Clippy.Models;
using Windows.Media.Capture;
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
    private bool _speechBlocked;
    private string _lastLoggedPhrase = "";
    private DateTime _lastRetryUtc = DateTime.MinValue;

    public bool IsListening => _isListening;
    public bool OnboardingMode { get; set; }
    public bool SpeechBlocked => _speechBlocked;
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

    public static void OpenSpeechPrivacySettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-speech")
            {
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    public static void OpenSpeechLanguageSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:speech")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            OpenSpeechPrivacySettings();
        }
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

        if (_speechBlocked)
        {
            StatusMessage = "Speech recognition blocked — turn on Windows speech / dictation in Settings";
            NotifyStateChanged();
            return;
        }

        try
        {
            await StopListeningAsync();

            if (!await EnsureMicrophoneAccessAsync())
            {
                _speechBlocked = true;
                LastVoiceError = "Microphone access was denied.";
                StatusMessage = "Allow microphone access in Windows Settings → Privacy → Microphone";
                ClippyDebugLog.Instance.Log("Voice", "Microphone access denied");
                NotifyStateChanged();
                return;
            }

            _recognizer = new SpeechRecognizer();

            // Deliberately no bare "Clippy" entry — a wake word on its own is not a command,
            // and listing it makes the recogniser emit it constantly.
            var grammar = new SpeechRecognitionListConstraint(new[]
            {
                "Clippy clip that",
                "Clippy clip this",
                "Clippy clip it",
                "Clippy do your thing",
                "clip that",
                "clip this",
                "clip it",
                "do your thing"
            });
            _recognizer.Constraints.Add(grammar);
            var compileResult = await _recognizer.CompileConstraintsAsync();
            if (compileResult.Status != SpeechRecognitionResultStatus.Success)
            {
                throw new InvalidOperationException($"Could not compile voice grammar: {compileResult.Status}");
            }

            _session = _recognizer.ContinuousRecognitionSession;
            _session.ResultGenerated += OnResultGenerated;
            _session.Completed += OnCompleted;

            await _session.StartAsync();
            _isListening = true;
            _speechBlocked = false;
            StatusMessage = "Listening for \"Clippy, clip that\"…";
            ClippyDebugLog.Instance.Log("Voice", "Starting recognition task");
        }
        catch (Exception ex)
        {
            LastVoiceError = ex.Message;
            if (IsSpeechPolicyError(ex))
            {
                _speechBlocked = true;
                StatusMessage = "Turn on Windows speech recognition and dictation in Settings";
                ClippyDebugLog.Instance.LogError("Voice", ex, "prepareAndStart (policy)");
                NotifyStateChanged();
                return;
            }

            StatusMessage = "Voice error — retrying…";
            ClippyDebugLog.Instance.LogError("Voice", ex, "prepareAndStart");

            var now = DateTime.UtcNow;
            if ((now - _lastRetryUtc).TotalSeconds < 30)
            {
                NotifyStateChanged();
                return;
            }

            _lastRetryUtc = now;
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
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
        if (!_speechBlocked)
        {
            StatusMessage = "Voice idle";
        }

        NotifyStateChanged();
    }

    public void ResetSpeechBlock()
    {
        _speechBlocked = false;
        _lastRetryUtc = DateTime.MinValue;
    }

    private void OnCompleted(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionCompletedEventArgs args)
    {
        if (_speechBlocked || !AppSettings.Instance.VoiceCommandsEnabled)
        {
            return;
        }

        _ = PrepareAndStartAsync();
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

        // Confidence alone is not enough to act on. The grammar contains the bare wake word,
        // so a high-confidence match fires on someone simply saying "Clippy" mid-sentence —
        // the phrase itself has to be a clip command.
        if (MatchesTrigger(text))
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

    private static async Task<bool> EnsureMicrophoneAccessAsync()
    {
        try
        {
            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio
            };

            // Must be disposed: an undisposed MediaCapture holds the microphone open for
            // the life of the process, which starves both the recogniser and the mic track
            // in recorded clips.
            using var capture = new MediaCapture();
            await capture.InitializeAsync(settings);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception ex)
        {
            ClippyDebugLog.Instance.Log("Voice", $"Microphone check failed: {ex.Message}");
            return false;
        }
    }

    private static bool IsSpeechPolicyError(Exception ex)
    {
        var message = ex.Message + " " + ex.HResult;
        return message.Contains("speech privacy policy", StringComparison.OrdinalIgnoreCase)
            || message.Contains("0x80045509", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="text"/> is an actual clip command.
    /// </summary>
    /// <remarks>
    /// A wake word by itself does not qualify. "Clippy" contains "clip", so a naive
    /// substring test treats someone merely saying the app's name as a command — the
    /// phrase needs a wake word plus a separate intent word, or a complete stock phrase.
    /// </remarks>
    public static bool MatchesTrigger(string text)
    {
        var words = text.ToLowerInvariant()
            .Replace(",", " ")
            .Replace(".", " ")
            .Replace("'", "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var joined = string.Join(' ', words);

        string[] explicitPhrases =
        {
            "do your thing",
            "do ya thing",
            "clip that",
            "clip this",
            "clip it",
            "clippy clip",
            "clippy do your"
        };

        if (explicitPhrases.Any(joined.Contains))
        {
            return true;
        }

        string[] intentWords = { "clip", "clips", "that", "this", "it", "thing", "thang" };

        var hasWake = false;
        var hasIntent = false;
        foreach (var word in words)
        {
            if (!hasWake && IsWakeWord(word))
            {
                hasWake = true;
                continue; // The wake word cannot double as its own intent word.
            }

            if (intentWords.Contains(word))
            {
                hasIntent = true;
            }
        }

        return hasWake && hasIntent;
    }

    private static bool IsWakeWord(string word) =>
        word.StartsWith("clipp", StringComparison.Ordinal) ||
        word.StartsWith("clipty", StringComparison.Ordinal);

    private void NotifyStateChanged() => StateChanged?.Invoke();
}
