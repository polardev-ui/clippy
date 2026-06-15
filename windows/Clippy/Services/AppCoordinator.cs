using Clippy.Models;

namespace Clippy.Services;

public sealed class AppCoordinator
{
    private static AppCoordinator? _instance;
    public static AppCoordinator Instance => _instance ??= new AppCoordinator();

    public bool ShowOnboarding { get; private set; } = !AppSettings.Instance.HasCompletedOnboarding;
    public bool ShowClipSavedBanner { get; private set; }
    public string? ErrorMessage { get; private set; }
    public Clip? LastClip { get; private set; }

    public event Action? StateChanged;

    private AppCoordinator()
    {
    }

    public void Bootstrap(nint windowHandle)
    {
        HotkeyManager.Instance.AttachWindow(windowHandle);
        HotkeyManager.Instance.OnTrigger = () => _ = TriggerClipAsync(ClipSource.Hotkey);
        HotkeyManager.Instance.Register(AppSettings.Instance.Hotkey);

        VoiceCommandListener.Instance.OnClipCommand = () => _ = TriggerClipAsync(ClipSource.Voice);
        VoiceCommandListener.Instance.OnOnboardingClipCommand = () => CompleteOnboarding(fromVoiceDemo: true);

        if (AppSettings.Instance.HasCompletedOnboarding)
        {
            StartBackgroundServices();
        }
        else
        {
            ScreenRecorder.Instance.RequestScreenCaptureAccess();
        }
    }

    public void StartBackgroundServices()
    {
        ScreenRecorder.Instance.RequestScreenCaptureAccess();
        _ = Task.Run(async () =>
        {
            await ScreenRecorder.Instance.StartCaptureAsync();
            if (AppSettings.Instance.VoiceCommandsEnabled)
            {
                await Task.Delay(2000);
                await VoiceCommandListener.Instance.PrepareAndStartAsync();
            }
        });
    }

    public void ApplyOnboardingAudioDevices()
    {
        var settings = AppSettings.Instance;
        if (!string.IsNullOrEmpty(settings.PreferredAudioOutputId))
        {
            AudioDeviceManager.SetDefaultOutputDevice(settings.PreferredAudioOutputId);
        }

        if (!string.IsNullOrEmpty(settings.PreferredMicrophoneId))
        {
            AudioDeviceManager.SetDefaultInputDevice(settings.PreferredMicrophoneId);
        }

        VoiceCommandListener.Instance.RefreshMicrophone();
    }

    public void BeginOnboardingVoicePractice()
    {
        ApplyOnboardingAudioDevices();
        AppSettings.Instance.VoiceCommandsEnabled = true;
        AppSettings.Instance.Persist();

        VoiceCommandListener.Instance.OnboardingMode = true;
        _ = Task.Run(async () =>
        {
            await ScreenRecorder.Instance.StartCaptureAsync();
            await Task.Delay(1000);
            await VoiceCommandListener.Instance.PrepareAndStartAsync();
        });
    }

    public void CompleteOnboarding(bool fromVoiceDemo)
    {
        if (fromVoiceDemo)
        {
            SoundPlayer.Instance.PlayClipSound();
        }

        VoiceCommandListener.Instance.OnboardingMode = false;
        AppSettings.Instance.HasCompletedOnboarding = true;
        AppSettings.Instance.VoiceCommandsEnabled = true;
        AppSettings.Instance.Persist();
        ShowOnboarding = false;
        NotifyStateChanged();
        StartBackgroundServices();

        _ = VoiceCommandListener.Instance.PrepareAndStartAsync();
    }

    public void RefreshHotkey() =>
        HotkeyManager.Instance.Register(AppSettings.Instance.Hotkey);

    public void RefreshVoiceListening()
    {
        VoiceCommandListener.Instance.ResetSpeechBlock();
        if (AppSettings.Instance.VoiceCommandsEnabled)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                await VoiceCommandListener.Instance.PrepareAndStartAsync();
            });
        }
        else
        {
            _ = VoiceCommandListener.Instance.StopListeningAsync();
        }
    }

    public void RefreshMicrophone() => VoiceCommandListener.Instance.RefreshMicrophone();

    public void RefreshDisplay() =>
        _ = ScreenRecorder.Instance.RestartCaptureAsync();

    public void RefreshCaptureQuality() =>
        _ = ScreenRecorder.Instance.RestartCaptureAsync();

    public enum ClipSource
    {
        Hotkey,
        Voice,
        Button
    }

    public async Task TriggerClipAsync(ClipSource source)
    {
        var recorder = ScreenRecorder.Instance;
        if (recorder.IsClipping) return;

        var required = AppSettings.Instance.ClipDuration.Seconds();
        if (!recorder.IsBufferReady)
        {
            ErrorMessage = "Buffer is still filling — wait until status shows Ready.";
            ClippyDebugLog.Instance.Log("Clip",
                $"Blocked — buffer not ready. {RecorderDiagnostics.Snapshot(recorder)}");
            NotifyStateChanged();
            return;
        }

        if (recorder.BufferedSeconds < required - 0.5)
        {
            ErrorMessage =
                $"Only {(int)recorder.BufferedSeconds}s buffered — wait for {(int)required}s before clipping.";
            ClippyDebugLog.Instance.Log("Clip",
                $"Blocked — insufficient buffer: {recorder.BufferedSeconds}s / {required}s");
            NotifyStateChanged();
            return;
        }

        ClippyDebugLog.Instance.Log("Clip", $"Triggered via {source}");
        SoundPlayer.Instance.PlayClipSound();

        try
        {
            var result = await recorder.CreateClipAsync(required);
            var clip = await ClipManager.Instance.AddClipAsync(result.Path, result.Duration);
            try { File.Delete(result.Path); } catch { }

            LastClip = clip;
            ClippyDebugLog.Instance.Log("Clip", $"Saved clip {clip.FileName} duration={result.Duration}s");
            ShowClipSavedBanner = true;
            NotifyStateChanged();

            _ = Task.Run(async () =>
            {
                await Task.Delay(2500);
                ShowClipSavedBanner = false;
                NotifyStateChanged();
            });
        }
        catch (Exception ex)
        {
            ClippyDebugLog.Instance.LogError("Clip", ex, $"triggerClip({source})");
            if (!string.IsNullOrEmpty(recorder.LastClipDebugSummary))
            {
                ClippyDebugLog.Instance.Log("Clip", recorder.LastClipDebugSummary);
            }

            VoiceDiagnostics.LogSnapshot(VoiceCommandListener.Instance);
            ErrorMessage = ex.Message;
            NotifyStateChanged();
        }
    }

    public void ClearError()
    {
        ErrorMessage = null;
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();
}
