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

            var grammar = new SpeechRecognitionListConstraint(new[]
            {
                "Clippy",
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

    private static async Task<bool> EnsureMicrophoneAccessAsync()
    {
        try
        {
            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio
            };
            var capture = new MediaCapture();
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
