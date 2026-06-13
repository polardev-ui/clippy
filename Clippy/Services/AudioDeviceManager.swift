import AVFoundation
import CoreAudio
import Foundation

struct AudioInputDevice: Identifiable, Codable, Equatable, Hashable {
    let uid: String
    let name: String

    var id: String { uid }

    static let systemDefault = AudioInputDevice(uid: "", name: "System Default")
}

struct AudioOutputDevice: Identifiable, Codable, Equatable, Hashable {
    let uid: String
    let name: String

    var id: String { uid }

    static let systemDefault = AudioOutputDevice(uid: "", name: "System Default")
}

enum AudioDeviceManager {
    static func refreshDevices() -> [AudioInputDevice] {
        [.systemDefault] + discoverInputDevices()
    }

    static func refreshOutputDevices() -> [AudioOutputDevice] {
        [.systemDefault] + discoverOutputDevices()
    }

    static func deviceName(for uid: String?, in devices: [AudioInputDevice]) -> String {
        guard let uid, !uid.isEmpty else { return AudioInputDevice.systemDefault.name }
        return devices.first { $0.uid == uid }?.name ?? "Unknown Microphone"
    }

    static func applyPreferredInput(to inputNode: AVAudioInputNode, preferredUID: String) {
        guard let resolved = resolveInputUID(preferredUID),
              let deviceID = audioDeviceID(forUID: resolved) else { return }
        setSystemDefaultInputDevice(deviceID)
    }

    static func setSystemDefaultInputDevice(uid: String) {
        guard let resolved = resolveInputUID(uid),
              let deviceID = audioDeviceID(forUID: resolved) else { return }
        setSystemDefaultInputDevice(deviceID)
    }

    static func setSystemDefaultOutputDevice(uid: String) {
        guard let resolved = resolveOutputUID(uid),
              let deviceID = audioDeviceID(forUID: resolved) else { return }
        setSystemDefaultOutputDevice(deviceID)
    }

    static func resolveOutputUID(_ preferred: String) -> String? {
        guard !preferred.isEmpty else { return nil }
        if audioDeviceID(forUID: preferred) != nil { return preferred }

        let devices = discoverOutputDevices()
        if let byName = devices.first(where: { $0.name == preferred || $0.uid == preferred }) {
            return byName.uid
        }
        return nil
    }

    static func resolvedOutputDeviceName(for uid: String) -> String {
        guard let resolved = resolveOutputUID(uid),
              let id = audioDeviceID(forUID: resolved),
              let name = deviceName(id) else {
            return deviceOutputName(for: uid, in: refreshOutputDevices())
        }
        return name
    }

    static func deviceOutputName(for uid: String?, in devices: [AudioOutputDevice]) -> String {
        guard let uid, !uid.isEmpty else { return AudioOutputDevice.systemDefault.name }
        return devices.first { $0.uid == uid }?.name ?? "Unknown Output"
    }

    static func avCaptureMicrophoneID(forPreferredUID uid: String) -> String? {
        let session = AVCaptureDevice.DiscoverySession(
            deviceTypes: [.microphone, .external],
            mediaType: .audio,
            position: .unspecified
        )
        if !uid.isEmpty {
            if let resolved = resolveInputUID(uid) {
                if let match = session.devices.first(where: { $0.uniqueID == resolved }) {
                    return match.uniqueID
                }
                let targetName = resolvedDeviceName(for: uid)
                if let match = session.devices.first(where: { $0.localizedName == targetName }) {
                    return match.uniqueID
                }
            }
        }
        return AVCaptureDevice.default(for: .audio)?.uniqueID
    }

    static func resolveInputUID(_ preferred: String) -> String? {
        guard !preferred.isEmpty else { return nil }
        if audioDeviceID(forUID: preferred) != nil { return preferred }

        let devices = discoverInputDevices()
        if let byName = devices.first(where: { $0.name == preferred || $0.uid == preferred }) {
            return byName.uid
        }

        let lowered = preferred.lowercased()
        if lowered.contains("builtin") || lowered.contains("built-in") || lowered.contains("builtInmicrophone") {
            if let builtIn = devices.first(where: {
                let name = $0.name.lowercased()
                return name.contains("macbook") || name.contains("built-in") || name.contains("internal")
            }) {
                return builtIn.uid
            }
        }
        return nil
    }

    private static func setSystemDefaultInputDevice(_ deviceID: AudioDeviceID) {
        var id = deviceID
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDefaultInputDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        AudioObjectSetPropertyData(
            AudioObjectID(kAudioObjectSystemObject),
            &address,
            0,
            nil,
            UInt32(MemoryLayout<AudioDeviceID>.size),
            &id
        )
    }

    private static func setSystemDefaultOutputDevice(_ deviceID: AudioDeviceID) {
        var id = deviceID
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDefaultOutputDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        AudioObjectSetPropertyData(
            AudioObjectID(kAudioObjectSystemObject),
            &address,
            0,
            nil,
            UInt32(MemoryLayout<AudioDeviceID>.size),
            &id
        )
    }

    private static func discoverOutputDevices() -> [AudioOutputDevice] {
        hardwareDeviceIDs().compactMap { id in
            guard hasOutputChannels(id),
                  let uid = deviceUID(id),
                  let name = deviceName(id),
                  !isAggregateOrVirtualDevice(name) else { return nil }
            return AudioOutputDevice(uid: uid, name: name)
        }
        .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    private static func hasOutputChannels(_ deviceID: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreamConfiguration,
            mScope: kAudioDevicePropertyScopeOutput,
            mElement: kAudioObjectPropertyElementMain
        )
        var dataSize: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(deviceID, &address, 0, nil, &dataSize) == noErr else { return false }
        let bufferListPointer = UnsafeMutablePointer<AudioBufferList>.allocate(capacity: Int(dataSize))
        defer { bufferListPointer.deallocate() }
        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, bufferListPointer) == noErr else { return false }
        return UnsafeMutableAudioBufferListPointer(bufferListPointer).contains { $0.mNumberChannels > 0 }
    }

    private static func discoverInputDevices() -> [AudioInputDevice] {
        hardwareDeviceIDs().compactMap { id in
            guard hasInputChannels(id),
                  let uid = deviceUID(id),
                  let name = deviceName(id),
                  !isAggregateOrVirtualDevice(name) else { return nil }
            return AudioInputDevice(uid: uid, name: name)
        }
        .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    private static func hardwareDeviceIDs() -> [AudioDeviceID] {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var dataSize: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &dataSize) == noErr else {
            return []
        }
        let count = Int(dataSize) / MemoryLayout<AudioDeviceID>.size
        var ids = [AudioDeviceID](repeating: 0, count: count)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &dataSize, &ids) == noErr else {
            return []
        }
        return ids
    }

    private static func hasInputChannels(_ deviceID: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreamConfiguration,
            mScope: kAudioDevicePropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain
        )
        var dataSize: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(deviceID, &address, 0, nil, &dataSize) == noErr else { return false }
        let bufferListPointer = UnsafeMutablePointer<AudioBufferList>.allocate(capacity: Int(dataSize))
        defer { bufferListPointer.deallocate() }
        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, bufferListPointer) == noErr else { return false }
        return UnsafeMutableAudioBufferListPointer(bufferListPointer).contains { $0.mNumberChannels > 0 }
    }

    private static func deviceUID(_ deviceID: AudioDeviceID) -> String? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDeviceUID,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var uid = "" as CFString
        var dataSize = UInt32(MemoryLayout<CFString>.size)
        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, &uid) == noErr else { return nil }
        return uid as String
    }

    private static func deviceName(_ deviceID: AudioDeviceID) -> String? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDeviceNameCFString,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var name = "" as CFString
        var dataSize = UInt32(MemoryLayout<CFString>.size)
        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, &name) == noErr else { return nil }
        return name as String
    }

    private static func audioDeviceID(forUID uid: String) -> AudioDeviceID? {
        hardwareDeviceIDs().first { deviceUID($0) == uid }
    }

    static func resolvedDeviceName(for uid: String) -> String {
        guard let resolved = resolveInputUID(uid),
              let id = audioDeviceID(forUID: resolved),
              let name = deviceName(id) else {
            return deviceName(for: uid, in: refreshDevices())
        }
        return name
    }

    private static func isAggregateOrVirtualDevice(_ name: String) -> Bool {
        let lowered = name.lowercased()
        return lowered.contains("aggregate")
            || lowered.contains("multi-output")
            || lowered.contains("soundflower")
            || lowered.contains("blackhole")
    }
}

@MainActor
final class AudioDeviceStore: ObservableObject {
    static let shared = AudioDeviceStore()

    @Published private(set) var inputDevices: [AudioInputDevice] = [.systemDefault]
    @Published private(set) var outputDevices: [AudioOutputDevice] = [.systemDefault]

    private init() {}

    func refreshDevices() {
        inputDevices = AudioDeviceManager.refreshDevices()
        outputDevices = AudioDeviceManager.refreshOutputDevices()
    }
}
