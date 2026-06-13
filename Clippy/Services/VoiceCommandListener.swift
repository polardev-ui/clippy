import AVFoundation
import CoreMedia
import Foundation
import Speech

@MainActor
final class VoiceCommandListener: NSObject, ObservableObject {
    static let shared = VoiceCommandListener()

    @Published private(set) var isListening = false
    @Published private(set) var authorizationStatus: SFSpeechRecognizerAuthorizationStatus = .notDetermined
    @Published private(set) var microphoneAuthorized = false
    @Published private(set) var lastHeardPhrase: String?
    @Published private(set) var activeMicrophoneName = AudioInputDevice.systemDefault.name
    @Published private(set) var statusMessage = "Voice idle"
    @Published private(set) var lastVoiceError: String?

    var onClipCommand: (() -> Void)?
    var onOnboardingClipCommand: (() -> Void)?
    var onboardingMode = false

    private let recognizer = SFSpeechRecognizer(locale: Locale(identifier: "en-US"))
    private let audioCapture = VoiceAudioCapture()
    private let audioConverter = SpeechAudioConverter()
    private let speechQueue = DispatchQueue(label: "com.clippy.voice.speech", qos: .userInitiated)

    private var activeRecognitionRequest: SFSpeechAudioBufferRecognitionRequest?
    private var recognitionTask: SFSpeechRecognitionTask?
    private var restartWorkItem: DispatchWorkItem?
    private var hasTriggeredThisUtterance = false
    private var listenGeneration = 0
    private var activeListenTask: Task<Void, Never>?
    private var lastLoggedPhrase = ""
    private var usesSharedCaptureMicrophone = false

    private override init() {
        super.init()
        authorizationStatus = SFSpeechRecognizer.authorizationStatus()
    }

    private func logVoice(_ message: String) {
        ClippyDebugLog.shared.log("Voice", message)
    }

    func prepareAndStart() async {
        logVoice("prepareAndStart")
        _ = await requestMicrophoneAccess()
        VoiceDiagnostics.logSnapshot(voice: self)
        if authorizationStatus == .notDetermined {
            logVoice("Requesting speech authorization…")
            requestAuthorization()
        } else {
            startListeningIfEnabled()
        }
    }

    func requestAuthorization() {
        SFSpeechRecognizer.requestAuthorization { [weak self] status in
            Task { @MainActor in
                guard let self else { return }
                self.authorizationStatus = status
                self.logVoice("Speech authorization → \(status.rawValue)")
                if status == .authorized {
                    self.startListeningIfEnabled()
                } else {
                    self.statusMessage = "Speech permission required for voice commands"
                    self.lastVoiceError = "Speech authorization: \(status.rawValue)"
                }
            }
        }
    }

    func requestMicrophoneAccess() async -> Bool {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            microphoneAuthorized = true
            return true
        case .notDetermined:
            let granted = await AVCaptureDevice.requestAccess(for: .audio)
            microphoneAuthorized = granted
            logVoice("Microphone access requested → \(granted)")
            return granted
        default:
            microphoneAuthorized = false
            statusMessage = "Microphone permission required for voice commands"
            lastVoiceError = "Microphone denied in System Settings"
            logVoice("Microphone access denied")
            return false
        }
    }

    func startListeningIfEnabled() {
        guard AppSettings.shared.voiceCommandsEnabled else {
            logVoice("Voice commands disabled in settings — not starting")
            activeListenTask?.cancel()
            activeListenTask = Task { await stopListening() }
            return
        }

        if authorizationStatus == .notDetermined {
            requestAuthorization()
            return
        }

        guard authorizationStatus == .authorized else {
            statusMessage = "Enable Speech Recognition in System Settings"
            lastVoiceError = "Speech not authorized (\(authorizationStatus.rawValue))"
            logVoice(lastVoiceError ?? "speech not authorized")
            return
        }

        activeListenTask?.cancel()
        activeListenTask = Task { await startListening() }
    }

    func stopListening() async {
        listenGeneration += 1
        restartWorkItem?.cancel()
        restartWorkItem = nil
        tearDownRecognitionTask()
        await audioCapture.stop()
        isListening = false
        hasTriggeredThisUtterance = false
        lastLoggedPhrase = ""
        usesSharedCaptureMicrophone = false
        statusMessage = AppSettings.shared.voiceCommandsEnabled ? "Voice paused" : "Voice commands off"
    }

    func refreshMicrophone() {
        AudioDeviceStore.shared.refreshDevices()
        let preferredUID = AppSettings.shared.preferredMicrophoneUID
        activeMicrophoneName = AudioDeviceManager.resolvedDeviceName(for: preferredUID)
        logVoice("Microphone changed → \(activeMicrophoneName)")
        Task { await restartWithSelectedMicrophone() }
    }

    func enableSharedCaptureMicrophoneIfNeeded() async {
        guard AppSettings.shared.voiceCommandsEnabled, isListening else { return }
        guard !usesSharedCaptureMicrophone else { return }
        logVoice("Switching voice to shared ScreenCaptureKit microphone")
        await stopListening()
        try? await Task.sleep(nanoseconds: 200_000_000)
        startListeningIfEnabled()
    }

    func ingestSharedCaptureMicrophoneSample(_ sampleBuffer: CMSampleBuffer) {
        SharedMicIngest.ingest(sampleBuffer)
    }

    nonisolated func ingestSharedCaptureMicrophoneSampleNonisolated(_ sampleBuffer: CMSampleBuffer) {
        SharedMicIngest.ingest(sampleBuffer)
    }

    private func restartWithSelectedMicrophone() async {
        guard AppSettings.shared.voiceCommandsEnabled else {
            await stopListening()
            return
        }
        await stopListening()
        try? await Task.sleep(nanoseconds: 300_000_000)
        startListeningIfEnabled()
    }

    private func startListening() async {
        await stopListening()
        listenGeneration += 1
        let generation = listenGeneration

        guard AppSettings.shared.voiceCommandsEnabled else { return }
        guard !Task.isCancelled else { return }
        guard let recognizer, recognizer.isAvailable else {
            statusMessage = "Speech recognizer unavailable"
            lastVoiceError = "SFSpeechRecognizer unavailable (isAvailable=false)"
            logVoice(lastVoiceError ?? "recognizer unavailable")
            scheduleRestart(after: 3)
            return
        }

        guard await requestMicrophoneAccess() else { return }
        guard generation == listenGeneration, !Task.isCancelled else { return }

        AudioDeviceStore.shared.refreshDevices()
        let preferredUID = AppSettings.shared.preferredMicrophoneUID
        let resolvedUID = AudioDeviceManager.resolveInputUID(preferredUID) ?? preferredUID
        activeMicrophoneName = AudioDeviceManager.resolvedDeviceName(for: preferredUID)
        logVoice("Mic preference=\(preferredUID) resolved=\(resolvedUID)")

        hasTriggeredThisUtterance = false
        lastLoggedPhrase = ""

        if ScreenRecorder.shared.isCapturing {
            usesSharedCaptureMicrophone = true
            isListening = true
            lastVoiceError = nil
            statusMessage = "Listening for \"Clippy, clip that\"… (shared mic)"
            logVoice("Listening active via shared capture mic")
            beginRecognitionTask(generation: generation)
            return
        }

        usesSharedCaptureMicrophone = false
        let converter = audioConverter
        let speechQueue = self.speechQueue
        do {
            let captureUID = AudioDeviceManager.resolveInputUID(preferredUID) ?? (preferredUID.isEmpty ? "" : preferredUID)
            try await audioCapture.start(preferredUID: captureUID) { [weak self] buffer in
                guard let converted = converter.convert(buffer) else { return }
                speechQueue.async { [weak self] in
                    self?.activeRecognitionRequest?.append(converted)
                }
            }
            guard generation == listenGeneration, !Task.isCancelled else {
                await audioCapture.stop()
                logVoice("Listen cancelled during startup")
                return
            }

            isListening = true
            lastVoiceError = nil
            statusMessage = "Listening for \"Clippy, clip that\"…"
            logVoice("Listening active")
            beginRecognitionTask(generation: generation)
        } catch {
            lastVoiceError = error.localizedDescription
            ClippyDebugLog.shared.logError("Voice", error, context: "audioCapture.start")
            statusMessage = error.localizedDescription
            await stopListening()
            scheduleRestart(after: 3)
        }
    }

    private func beginRecognitionTask(generation: Int) {
        guard generation == listenGeneration, isListening else { return }
        guard let recognizer, recognizer.isAvailable else { return }

        tearDownRecognitionTask()
        hasTriggeredThisUtterance = false

        let request = SFSpeechAudioBufferRecognitionRequest()
        request.shouldReportPartialResults = true
        request.taskHint = .dictation
        request.contextualStrings = [
            "Clippy",
            "clip that",
            "clip this",
            "clip it",
            "do your thing"
        ]
        if #available(macOS 13.0, *) {
            request.requiresOnDeviceRecognition = false
            logVoice("requiresOnDeviceRecognition=false (server recognition)")
        }

        activeRecognitionRequest = request
        if usesSharedCaptureMicrophone {
            SharedMicIngest.setRouting(enabled: true, request: request)
        }
        logVoice("Starting recognition task")

        recognitionTask = recognizer.recognitionTask(with: request) { [weak self] result, error in
            Task { @MainActor in
                await self?.handleRecognitionCallback(result: result, error: error, generation: generation)
            }
        }
    }

    private func handleRecognitionCallback(
        result: SFSpeechRecognitionResult?,
        error: Error?,
        generation: Int
    ) async {
        guard generation == listenGeneration, isListening else { return }

        if let result {
            let text = result.bestTranscription.formattedString
            lastHeardPhrase = text
            if text != lastLoggedPhrase, !text.isEmpty {
                lastLoggedPhrase = text
                logVoice("Heard: \"\(text)\"")
            }

            if matchesTrigger(in: text), !hasTriggeredThisUtterance {
                hasTriggeredThisUtterance = true
                statusMessage = "Heard clip command!"
                logVoice("Trigger matched in: \"\(text)\"")
                if onboardingMode {
                    onOnboardingClipCommand?()
                } else {
                    onClipCommand?()
                }
                await stopListening()
                scheduleRestart(after: 1.5)
                return
            }

            if result.isFinal, !hasTriggeredThisUtterance {
                logVoice("Utterance ended without trigger — restarting recognition")
                await restartRecognitionTask(generation: generation)
                return
            }
        }

        guard let error = error as NSError? else { return }

        if shouldRestartAfterRecognitionError(error) {
            logVoice("Recognition cycle ended (\(error.domain) \(error.code)) — restarting")
            await restartRecognitionTask(generation: generation)
            return
        }

        lastVoiceError = "\(error.domain) \(error.code): \(error.localizedDescription)"
        ClippyDebugLog.shared.logError("Voice", error, context: "recognitionTask")
        statusMessage = "Voice error — retrying…"
        await stopListening()
        scheduleRestart(after: 3)
    }

    private func shouldRestartAfterRecognitionError(_ error: NSError) -> Bool {
        if error.code == 1110 { return true }
        if error.domain == "kAFAssistantErrorDomain" {
            return [216, 217, 1101, 1107].contains(error.code)
        }
        if error.domain == "kLSRErrorDomain", error.code == 301 { return true }
        return false
    }

    private func restartRecognitionTask(generation: Int) async {
        guard generation == listenGeneration, isListening else { return }
        tearDownRecognitionTask()
        hasTriggeredThisUtterance = false
        lastLoggedPhrase = ""
        try? await Task.sleep(nanoseconds: 250_000_000)
        guard generation == listenGeneration, isListening else { return }
        beginRecognitionTask(generation: generation)
    }

    private func tearDownRecognitionTask() {
        SharedMicIngest.setRouting(enabled: false, request: nil)
        recognitionTask?.cancel()
        recognitionTask = nil
        activeRecognitionRequest?.endAudio()
        activeRecognitionRequest = nil
    }

    private func matchesTrigger(in text: String) -> Bool {
        let normalized = text
            .lowercased()
            .replacingOccurrences(of: ",", with: " ")
            .replacingOccurrences(of: ".", with: " ")
            .replacingOccurrences(of: "'", with: "")
            .components(separatedBy: .whitespacesAndNewlines)
            .filter { !$0.isEmpty }
            .joined(separator: " ")

        let explicitPhrases = [
            "do your thing",
            "do ya thing",
            "clip that",
            "clip this",
            "clip it",
            "clippy clip",
            "clippy do"
        ]
        if explicitPhrases.contains(where: { normalized.contains($0) }) {
            return true
        }

        let hasWake = normalized.contains("clipp") || normalized.contains("clippy") || normalized.contains("clipty")
        let hasClipIntent = normalized.contains("clip") || normalized.contains("thing")
        return hasWake && hasClipIntent
    }

    private func scheduleRestart(after delay: TimeInterval) {
        guard AppSettings.shared.voiceCommandsEnabled else { return }
        restartWorkItem?.cancel()
        let work = DispatchWorkItem { [weak self] in
            Task { @MainActor in
                self?.startListeningIfEnabled()
            }
        }
        restartWorkItem = work
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: work)
    }
}

private enum SharedMicIngest {
    private static let converter = SpeechAudioConverter()
    private static let speechQueue = DispatchQueue(label: "com.clippy.voice.shared-mic", qos: .userInitiated)
    private static let lock = NSLock()
    private static nonisolated(unsafe) var enabled = false
    private static nonisolated(unsafe) var request: SFSpeechAudioBufferRecognitionRequest?

    static func setRouting(enabled: Bool, request: SFSpeechAudioBufferRecognitionRequest?) {
        lock.lock()
        self.enabled = enabled
        self.request = request
        lock.unlock()
    }

    static func ingest(_ sampleBuffer: CMSampleBuffer) {
        lock.lock()
        let active = enabled
        let activeRequest = request
        lock.unlock()
        guard active, let activeRequest else { return }
        guard let pcm = CaptureAudioSampleConverter.pcmBuffer(from: sampleBuffer),
              let converted = converter.convert(pcm) else { return }
        speechQueue.async {
            activeRequest.append(converted)
        }
    }
}
