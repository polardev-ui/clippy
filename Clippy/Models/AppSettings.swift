import Combine
import Foundation
import SwiftUI

@MainActor
final class AppSettings: ObservableObject {
    static let shared = AppSettings()

    @Published var clipDuration: ClipDuration {
        didSet { persist() }
    }

    @Published var hotkey: HotkeyBinding {
        didSet { persist() }
    }

    @Published var voiceCommandsEnabled: Bool {
        didSet { persist() }
    }

    @Published var soundEnabled: Bool {
        didSet { persist() }
    }

    @Published var preferredMicrophoneUID: String {
        didSet { persist() }
    }

    @Published var preferredAudioOutputUID: String {
        didSet { persist() }
    }

    @Published var preferredDisplayID: UInt32 {
        didSet { persist() }
    }

    @Published var captureResolution: CaptureResolution {
        didSet { persist() }
    }

    @Published var captureFrameRate: CaptureFrameRate {
        didSet { persist() }
    }

    @Published var hasCompletedOnboarding: Bool {
        didSet { persist() }
    }

    private let defaults = UserDefaults.standard
    private enum Keys {
        static let clipDuration = "clipDuration"
        static let hotkey = "hotkey"
        static let voiceCommandsEnabled = "voiceCommandsEnabled"
        static let soundEnabled = "soundEnabled"
        static let preferredMicrophoneUID = "preferredMicrophoneUID"
        static let preferredAudioOutputUID = "preferredAudioOutputUID"
        static let preferredDisplayID = "preferredDisplayID"
        static let captureResolution = "captureResolution"
        static let captureFrameRate = "captureFrameRate"
        static let hasCompletedOnboarding = "hasCompletedOnboarding"
    }

    private init() {
        let storedDuration = defaults.integer(forKey: Keys.clipDuration)
        clipDuration = ClipDuration(rawValue: storedDuration) ?? .thirty

        if let data = defaults.data(forKey: Keys.hotkey),
           let decoded = try? JSONDecoder().decode(HotkeyBinding.self, from: data) {
            hotkey = decoded
        } else {
            hotkey = .default
        }

        if defaults.object(forKey: Keys.voiceCommandsEnabled) != nil {
            voiceCommandsEnabled = defaults.bool(forKey: Keys.voiceCommandsEnabled)
        } else {
            voiceCommandsEnabled = false
        }

        if defaults.object(forKey: Keys.soundEnabled) != nil {
            soundEnabled = defaults.bool(forKey: Keys.soundEnabled)
        } else {
            soundEnabled = true
        }

        var storedMicUID = defaults.string(forKey: Keys.preferredMicrophoneUID) ?? ""
        if let resolved = AudioDeviceManager.resolveInputUID(storedMicUID), resolved != storedMicUID {
            storedMicUID = resolved
        }
        preferredMicrophoneUID = storedMicUID

        preferredAudioOutputUID = defaults.string(forKey: Keys.preferredAudioOutputUID) ?? ""

        if defaults.object(forKey: Keys.preferredDisplayID) != nil {
            preferredDisplayID = UInt32(defaults.integer(forKey: Keys.preferredDisplayID))
        } else {
            preferredDisplayID = CaptureDisplay.mainDisplayID
        }

        let storedResolution = defaults.integer(forKey: Keys.captureResolution)
        captureResolution = CaptureResolution(rawValue: storedResolution) ?? .p720

        let storedFrameRate = defaults.integer(forKey: Keys.captureFrameRate)
        captureFrameRate = CaptureFrameRate(rawValue: storedFrameRate) ?? .fps30

        hasCompletedOnboarding = defaults.bool(forKey: Keys.hasCompletedOnboarding)
    }

    private func persist() {
        defaults.set(clipDuration.rawValue, forKey: Keys.clipDuration)
        if let data = try? JSONEncoder().encode(hotkey) {
            defaults.set(data, forKey: Keys.hotkey)
        }
        defaults.set(voiceCommandsEnabled, forKey: Keys.voiceCommandsEnabled)
        defaults.set(soundEnabled, forKey: Keys.soundEnabled)
        defaults.set(preferredMicrophoneUID, forKey: Keys.preferredMicrophoneUID)
        defaults.set(preferredAudioOutputUID, forKey: Keys.preferredAudioOutputUID)
        defaults.set(Int(preferredDisplayID), forKey: Keys.preferredDisplayID)
        defaults.set(captureResolution.rawValue, forKey: Keys.captureResolution)
        defaults.set(captureFrameRate.rawValue, forKey: Keys.captureFrameRate)
        defaults.set(hasCompletedOnboarding, forKey: Keys.hasCompletedOnboarding)
    }
}
