import Foundation
import SwiftUI

@MainActor
final class AppCoordinator: ObservableObject {
    static let shared = AppCoordinator()

    @Published var lastClip: Clip?
    @Published var showClipSavedBanner = false
    @Published var errorMessage: String?
    @Published var showOnboarding: Bool

    private let settings = AppSettings.shared
    private let recorder = ScreenRecorder.shared
    private let clips = ClipManager.shared
    private let voice = VoiceCommandListener.shared

    private init() {
        showOnboarding = !AppSettings.shared.hasCompletedOnboarding
    }

    func bootstrap() {
        HotkeyManager.shared.onTrigger = { [weak self] in
            Task { @MainActor in
                await self?.triggerClip(source: .hotkey)
            }
        }
        HotkeyManager.shared.register(binding: settings.hotkey)

        voice.onClipCommand = { [weak self] in
            ClippyDebugLog.shared.log("Clip", "Voice command received")
            Task { @MainActor in
                await self?.triggerClip(source: .voice)
            }
        }

        voice.onOnboardingClipCommand = { [weak self] in
            Task { @MainActor in
                self?.completeOnboarding(fromVoiceDemo: true)
            }
        }

        if settings.hasCompletedOnboarding {
            startBackgroundServices()
        } else {
            recorder.requestScreenCaptureAccess()
        }
    }

    func startBackgroundServices() {
        recorder.requestScreenCaptureAccess()

        Task {
            await DisplayStore.shared.refreshDisplays()
            await recorder.startCapture()
            if settings.voiceCommandsEnabled {
                try? await Task.sleep(nanoseconds: 2_000_000_000)
                await voice.prepareAndStart()
            }
        }
    }

    func applyOnboardingAudioDevices() {
        if !settings.preferredAudioOutputUID.isEmpty {
            AudioDeviceManager.setSystemDefaultOutputDevice(uid: settings.preferredAudioOutputUID)
        }
        if !settings.preferredMicrophoneUID.isEmpty {
            AudioDeviceManager.setSystemDefaultInputDevice(uid: settings.preferredMicrophoneUID)
        }
        voice.refreshMicrophone()
    }

    func beginOnboardingVoicePractice() {
        applyOnboardingAudioDevices()
        settings.voiceCommandsEnabled = true

        Task {
            await DisplayStore.shared.refreshDisplays()
            await recorder.startCapture()
            try? await Task.sleep(nanoseconds: 1_000_000_000)
            voice.onboardingMode = true
            await voice.prepareAndStart()
        }
    }

    func completeOnboarding(fromVoiceDemo: Bool) {
        if fromVoiceDemo {
            SoundPlayer.shared.playClipSound()
        }
        voice.onboardingMode = false
        settings.hasCompletedOnboarding = true
        settings.voiceCommandsEnabled = true
        withAnimation(ClippyTheme.spring) {
            showOnboarding = false
        }
        Task {
            await voice.prepareAndStart()
        }
    }

    func refreshHotkey() {
        HotkeyManager.shared.register(binding: settings.hotkey)
    }

    func refreshVoiceListening() {
        if settings.voiceCommandsEnabled {
            Task {
                try? await Task.sleep(nanoseconds: 500_000_000)
                await voice.prepareAndStart()
            }
        } else {
            Task { await voice.stopListening() }
        }
    }

    func refreshMicrophone() {
        voice.refreshMicrophone()
    }

    func refreshDisplay() {
        Task {
            await DisplayStore.shared.refreshDisplays()
            await recorder.restartCapture()
        }
    }

    func refreshCaptureQuality() {
        Task {
            await recorder.restartCapture()
        }
    }

    enum ClipSource {
        case hotkey
        case voice
        case button
    }

    func triggerClip(source: ClipSource) async {
        guard !recorder.isClipping else { return }
        let requiredSeconds = settings.clipDuration.seconds
        guard recorder.isBufferReady else {
            let msg = "Buffer is still filling — wait until status shows Ready."
            ClippyDebugLog.shared.log("Clip", "Blocked — buffer not ready. \(RecorderDiagnostics.snapshot(recorder: recorder))")
            errorMessage = msg
            return
        }
        guard recorder.bufferedSeconds >= requiredSeconds - 0.5 else {
            let msg = "Only \(Int(recorder.bufferedSeconds))s buffered — wait for \(Int(requiredSeconds))s before clipping."
            ClippyDebugLog.shared.log("Clip", "Blocked — insufficient buffer: \(recorder.bufferedSeconds)s / \(requiredSeconds)s")
            errorMessage = msg
            return
        }

        ClippyDebugLog.shared.log("Clip", "Triggered via \(source)")
        SoundPlayer.shared.playClipSound()

        do {
            let result = try await recorder.createClip(maxDuration: settings.clipDuration.seconds)
            let clip = try await clips.addClip(from: result.url, duration: result.duration)
            try? FileManager.default.removeItem(at: result.url)
            lastClip = clip
            ClippyDebugLog.shared.log("Clip", "Saved clip \(clip.fileName) duration=\(result.duration)s")
            withAnimation(ClippyTheme.springBouncy) {
                showClipSavedBanner = true
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) {
                withAnimation(ClippyTheme.easeOut) {
                    self.showClipSavedBanner = false
                }
            }
        } catch {
            ClippyDebugLog.shared.logError("Clip", error, context: "triggerClip(\(source))")
            if !recorder.lastClipDebugSummary.isEmpty {
                ClippyDebugLog.shared.log("Clip", recorder.lastClipDebugSummary)
            }
            VoiceDiagnostics.logSnapshot(voice: voice)
            errorMessage = error.localizedDescription
        }
    }
}
