import AVFoundation
import CoreMedia
import Foundation
import ScreenCaptureKit

struct RecordingSegment {
    let url: URL
    let startTime: Date
    let duration: TimeInterval
    let frameCount: Int
}

enum ScreenRecorderError: LocalizedError {
    case noDisplay
    case permissionDenied
    case exportFailed(String?)
    case noSegments

    var errorDescription: String? {
        switch self {
        case .noDisplay: return "No display available for capture."
        case .permissionDenied: return "Screen Recording permission is required — enable Clippy in System Settings."
        case .exportFailed(let detail):
            if let detail, !detail.isEmpty { return detail }
            return "Failed to export the clip."
        case .noSegments: return "No recording buffer available yet — wait a few seconds for the buffer to fill. Open Settings → Debug Log for details."
        }
    }
}

private enum SCFrameValidator {
    static func isCompleteScreenFrame(_ sampleBuffer: CMSampleBuffer) -> Bool {
        guard sampleBuffer.isValid else { return false }
        guard let attachmentsArray = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let attachments = attachmentsArray.first else { return false }
        guard let statusRaw = attachments[SCStreamFrameInfo.status] as? Int,
              let status = SCFrameStatus(rawValue: statusRaw),
              status == .complete else { return false }
        guard CMSampleBufferGetImageBuffer(sampleBuffer) != nil else { return false }
        return true
    }
}

private enum CapturePipeline {
    static let writerQueue = DispatchQueue(label: "com.clippy.recorder.writer", qos: .userInitiated)

    static func copySampleBuffer(_ sampleBuffer: CMSampleBuffer) -> CMSampleBuffer? {
        var copy: CMSampleBuffer?
        let status = CMSampleBufferCreateCopy(
            allocator: kCFAllocatorDefault,
            sampleBuffer: sampleBuffer,
            sampleBufferOut: &copy
        )
        guard status == noErr else { return nil }
        return copy
    }
}

private enum SegmentAudioTrack {
    case system
    case microphone
}

private final class SegmentWriter: @unchecked Sendable {
    private var writer: AVAssetWriter?
    private var videoInput: AVAssetWriterInput?
    private var systemAudioInput: AVAssetWriterInput?
    private var micAudioInput: AVAssetWriterInput?
    private var segmentURL: URL?
    private var segmentStartedAt = Date()
    private var hasStartedSession = false
    private var isFinalizing = false
    private var frameIndex: Int64 = 0
    private var segmentMediaAnchor: CMTime?
    private var pausedForClip = false
    private var clipBoundaryWallTime: Date?

    private(set) var pendingFinalizationURLs: Set<URL> = []
    private(set) var currentSegmentURL: URL?

    private var systemAudioWritten: Int64 = 0
    private var micAudioWritten: Int64 = 0
    private var micPacketsReceived: Int64 = 0
    private var micCachedCount: Int64 = 0
    private var audioDisabled = false
    private var audioFailureCount = 0
    private var systemAudioReceivedCount: Int64 = 0
    private var micNormalizeFailCount: Int64 = 0
    private var loggedSystemFormat = false
    private var loggedMicFormat = false
    private var loggedMicNormalizeFailure = false
    private var lastWriterRecovery = Date.distantPast
    private let writerRecoveryCooldown: TimeInterval = 2.0
    private let writeLock = NSLock()

    private static let pcmAudioFormatHint: CMFormatDescription? = {
        var asbd = AudioStreamBasicDescription(
            mSampleRate: 48_000,
            mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
            mBytesPerPacket: 8,
            mFramesPerPacket: 1,
            mBytesPerFrame: 8,
            mChannelsPerFrame: 2,
            mBitsPerChannel: 32,
            mReserved: 0
        )
        var description: CMFormatDescription?
        CMAudioFormatDescriptionCreate(
            allocator: kCFAllocatorDefault,
            asbd: &asbd,
            layoutSize: 0,
            layout: nil,
            magicCookieSize: 0,
            magicCookie: nil,
            extensions: nil,
            formatDescriptionOut: &description
        )
        return description
    }()

    let segmentDuration: TimeInterval
    let captureFPS: Int
    let videoBitrate: Int
    let directory: URL
    let onSegmentFinished: (RecordingSegment?) -> Void

    init(
        directory: URL,
        segmentDuration: TimeInterval,
        captureFPS: Int,
        videoBitrate: Int,
        onSegmentFinished: @escaping (RecordingSegment?) -> Void
    ) {
        self.directory = directory
        self.segmentDuration = segmentDuration
        self.captureFPS = max(captureFPS, 1)
        self.videoBitrate = videoBitrate
        self.onSegmentFinished = onSegmentFinished
    }

    typealias SegmentCompletion = (RecordingSegment?) -> Void

    var writtenFrameCount: Int { Int(frameIndex) }

    private func openNewSegmentFile() {
        let url = directory.appendingPathComponent("seg_\(UUID().uuidString).mov")
        segmentURL = url
        currentSegmentURL = url
        segmentStartedAt = Date()
        hasStartedSession = false
        frameIndex = 0
        segmentMediaAnchor = nil
        systemAudioWritten = 0
        micAudioWritten = 0
        micPacketsReceived = 0
        micCachedCount = 0
        audioDisabled = false
        audioFailureCount = 0
        systemAudioReceivedCount = 0
        loggedSystemFormat = false
        loggedMicFormat = false
        loggedMicNormalizeFailure = false
        micNormalizeFailCount = 0
        videoInput = nil
        systemAudioInput = nil
        micAudioInput = nil

        if let writer = try? AVAssetWriter(outputURL: url, fileType: .mov) {
            writer.shouldOptimizeForNetworkUse = false
            self.writer = writer
        } else {
            self.writer = nil
        }
    }

    func setClipBoundary(wallTime: Date) {
        clipBoundaryWallTime = wallTime
        pausedForClip = true
    }

    func processVideo(_ sampleBuffer: CMSampleBuffer) {
        writeLock.lock()
        defer { writeLock.unlock() }
        processVideoLocked(sampleBuffer)
    }

    func processSystemAudio(_ sampleBuffer: CMSampleBuffer) {
        writeLock.lock()
        defer { writeLock.unlock() }
        processSystemAudioLocked(sampleBuffer)
    }

    func processMicrophoneAudio(_ sampleBuffer: CMSampleBuffer) {
        writeLock.lock()
        defer { writeLock.unlock() }
        processMicrophoneAudioLocked(sampleBuffer)
        if let copy = CapturePipeline.copySampleBuffer(sampleBuffer) {
            onMicrophoneSample?(copy)
        }
    }

    var audioDiagnostics: String {
        "systemAudioReceived=\(systemAudioReceivedCount) systemWritten=\(systemAudioWritten) " +
        "micReceived=\(micPacketsReceived) micWritten=\(micAudioWritten) " +
        "micCached=\(micCachedCount) micNormalizeFail=\(micNormalizeFailCount)"
    }

    func noteSystemAudioReceived() {
        systemAudioReceivedCount += 1
    }

    private func processVideoLocked(_ sampleBuffer: CMSampleBuffer) {
        if let boundary = clipBoundaryWallTime, Date() >= boundary { return }
        guard !pausedForClip else { return }
        guard SCFrameValidator.isCompleteScreenFrame(sampleBuffer) else { return }
        if writer == nil, !isFinalizing { openNewSegmentFile() }
        guard let writer, !isFinalizing else { return }
        if writer.status == .failed {
            recoverFromFailedWriter()
            return
        }

        if videoInput == nil {
            guard let formatDescription = CMSampleBufferGetFormatDescription(sampleBuffer) else { return }
            let dimensions = CMVideoFormatDescriptionGetDimensions(formatDescription)
            let width = max(2, Int(dimensions.width) & ~1)
            let height = max(2, Int(dimensions.height) & ~1)
            let settings: [String: Any] = [
                AVVideoCodecKey: AVVideoCodecType.h264,
                AVVideoWidthKey: width,
                AVVideoHeightKey: height,
                AVVideoCompressionPropertiesKey: [
                    AVVideoAverageBitRateKey: videoBitrate,
                    AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel,
                    AVVideoMaxKeyFrameIntervalKey: captureFPS * 2,
                    AVVideoAllowFrameReorderingKey: false
                ]
            ]
            let input = AVAssetWriterInput(
                mediaType: .video,
                outputSettings: settings,
                sourceFormatHint: formatDescription
            )
            input.expectsMediaDataInRealTime = true
            guard writer.canAdd(input) else { return }
            writer.add(input)
            videoInput = input
            ensureAudioInputs(on: writer)
        }

        let samplePTS = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        if segmentMediaAnchor == nil { segmentMediaAnchor = samplePTS }

        startSessionIfNeeded(on: writer)

        guard hasStartedSession else { return }

        guard writer.status != .failed, let videoInput else { return }
        if let systemAudioInput, !systemAudioInput.isReadyForMoreMediaData { return }
        guard videoInput.isReadyForMoreMediaData else { return }

        guard let base = segmentMediaAnchor,
              let retimed = retimestampVideo(sampleBuffer, base: base) else { return }
        guard videoInput.append(retimed) else { return }
        frameIndex += 1

        if Date().timeIntervalSince(segmentStartedAt) >= segmentDuration {
            finalizeSegment(completion: nil)
        }
    }

    private func processSystemAudioLocked(_ sampleBuffer: CMSampleBuffer) {
        guard !pausedForClip, !audioDisabled else { return }
        guard isValidAudioSample(sampleBuffer) else { return }
        logAudioFormatOnce(sampleBuffer, track: .system)
        systemAudioReceivedCount += 1
        guard let pcm = SegmentAudioConverter.normalizedPCM(from: sampleBuffer) else { return }
        if appendPCM(pcm, from: sampleBuffer, to: .system) {
            systemAudioWritten += 1
        }
    }

    private func processMicrophoneAudioLocked(_ sampleBuffer: CMSampleBuffer) {
        guard !pausedForClip, !audioDisabled else { return }
        guard isValidAudioSample(sampleBuffer) else { return }
        logAudioFormatOnce(sampleBuffer, track: .microphone)
        micPacketsReceived += 1
        guard let pcm = SegmentAudioConverter.normalizedPCM(from: sampleBuffer) else {
            micNormalizeFailCount += 1
            logMicNormalizeFailureOnce(sampleBuffer)
            return
        }
        micCachedCount += 1
        if appendPCM(pcm, from: sampleBuffer, to: .microphone) {
            micAudioWritten += 1
        }
    }

    var onMicrophoneSample: ((CMSampleBuffer) -> Void)?

    @discardableResult
    private func appendPCM(
        _ pcm: AVAudioPCMBuffer,
        from sampleBuffer: CMSampleBuffer,
        to track: SegmentAudioTrack
    ) -> Bool {
        if let boundary = clipBoundaryWallTime, Date() >= boundary { return false }
        guard !pausedForClip, !audioDisabled else { return false }
        guard writer != nil, videoInput != nil, !isFinalizing else { return false }
        guard let writer, writer.status != .failed else { return false }

        startSessionIfNeeded(on: writer)
        guard hasStartedSession, segmentMediaAnchor != nil else { return false }

        let input: AVAssetWriterInput?
        switch track {
        case .system: input = systemAudioInput
        case .microphone: input = micAudioInput
        }
        guard let input, input.isReadyForMoreMediaData else { return false }

        guard let base = segmentMediaAnchor,
              let copy = CapturePipeline.copySampleBuffer(sampleBuffer),
              let retimed = retimestampAudio(copy, base: base) else {
            return false
        }

        let presentationTime = CMSampleBufferGetPresentationTimeStamp(retimed)
        let frameCount = Int(pcm.frameLength)
        let duration = CMTime(value: Int64(frameCount), timescale: 48_000)

        guard let prepared = SegmentAudioConverter.makeSampleBuffer(
            from: pcm,
            presentationTime: presentationTime,
            duration: duration
        ) else {
            return false
        }

        if input.append(prepared) {
            audioFailureCount = 0
            return true
        }

        audioFailureCount += 1
        if audioFailureCount >= 30 {
            disableAudio(reason: writer.error?.localizedDescription ?? "append failed")
        }
        return false
    }

    private func disableAudio(reason: String) {
        guard !audioDisabled else { return }
        audioDisabled = true
        systemAudioInput?.markAsFinished()
        micAudioInput?.markAsFinished()
        systemAudioInput = nil
        micAudioInput = nil
        logSegment("Audio disabled: \(reason)")
    }

    private func logMicNormalizeFailureOnce(_ sampleBuffer: CMSampleBuffer) {
        guard !loggedMicNormalizeFailure else { return }
        loggedMicNormalizeFailure = true
        guard let formatDescription = CMSampleBufferGetFormatDescription(sampleBuffer),
              let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(formatDescription) else {
            logSegment("Mic normalize failed (no format description)")
            return
        }
        let interleaved = (asbd.pointee.mFormatFlags & kAudioFormatFlagIsNonInterleaved) == 0
        logSegment(
            "Mic normalize failed — " +
            "\(Int(asbd.pointee.mSampleRate))Hz ch=\(asbd.pointee.mChannelsPerFrame) " +
            "bits=\(asbd.pointee.mBitsPerChannel) interleaved=\(interleaved) " +
            "formatID=\(asbd.pointee.mFormatID) samples=\(CMSampleBufferGetNumSamples(sampleBuffer))"
        )
    }

    private func logAudioFormatOnce(_ sampleBuffer: CMSampleBuffer, track: SegmentAudioTrack) {
        switch track {
        case .system: guard !loggedSystemFormat else { return }; loggedSystemFormat = true
        case .microphone: guard !loggedMicFormat else { return }; loggedMicFormat = true
        }
        guard let formatDescription = CMSampleBufferGetFormatDescription(sampleBuffer),
              let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(formatDescription) else {
            return
        }
        let label = track == .system ? "System" : "Mic"
        let interleaved = (asbd.pointee.mFormatFlags & kAudioFormatFlagIsNonInterleaved) == 0
        logSegment(
            "\(label) audio format: \(Int(asbd.pointee.mSampleRate))Hz ch=\(asbd.pointee.mChannelsPerFrame) " +
            "interleaved=\(interleaved) formatID=\(asbd.pointee.mFormatID)"
        )
    }

    private func recoverFromFailedWriter() {
        guard !isFinalizing else { return }
        let now = Date()
        guard now.timeIntervalSince(lastWriterRecovery) >= writerRecoveryCooldown else { return }
        lastWriterRecovery = now

        let errorDetail = writer?.error?.localizedDescription ?? "unknown"
        logSegment("Writer failed (\(errorDetail)) — recovering segment")
        guard let writer, let url = segmentURL else {
            openNewSegmentFile()
            return
        }
        videoInput?.markAsFinished()
        systemAudioInput?.markAsFinished()
        micAudioInput?.markAsFinished()
        self.writer = nil
        videoInput = nil
        systemAudioInput = nil
        micAudioInput = nil
        segmentURL = nil
        hasStartedSession = false
        segmentMediaAnchor = nil
        pendingFinalizationURLs.remove(url)
        try? FileManager.default.removeItem(at: url)
        isFinalizing = false
        openNewSegmentFile()
    }

    private func logSegment(_ message: String) {
        Task { @MainActor in
            ClippyDebugLog.shared.log("Recorder", message)
        }
    }

    private func ensureAudioInputs(on writer: AVAssetWriter) {
        guard !hasStartedSession else { return }
        if systemAudioInput == nil {
            systemAudioInput = makePCMAudioInput(on: writer)
        }
        if micAudioInput == nil {
            micAudioInput = makePCMAudioInput(on: writer)
        }
    }

    private func makePCMAudioInput(on writer: AVAssetWriter) -> AVAssetWriterInput? {
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: 48_000,
            AVNumberOfChannelsKey: 2,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsNonInterleaved: false
        ]
        let input = AVAssetWriterInput(
            mediaType: .audio,
            outputSettings: settings,
            sourceFormatHint: Self.pcmAudioFormatHint
        )
        input.expectsMediaDataInRealTime = true
        guard writer.canAdd(input) else {
            logSegment("Could not add PCM audio input to segment writer")
            return nil
        }
        writer.add(input)
        return input
    }

    private func isValidAudioSample(_ sampleBuffer: CMSampleBuffer) -> Bool {
        guard CMSampleBufferIsValid(sampleBuffer) else { return false }
        if CMSampleBufferGetFormatDescription(sampleBuffer) == nil { return false }
        if !CMSampleBufferDataIsReady(sampleBuffer) {
            _ = CMSampleBufferMakeDataReady(sampleBuffer)
        }
        if CMSampleBufferGetNumSamples(sampleBuffer) > 0 { return true }
        if CMSampleBufferGetTotalSampleSize(sampleBuffer) > 0 { return true }
        if let blockBuffer = CMSampleBufferGetDataBuffer(sampleBuffer),
           CMBlockBufferGetDataLength(blockBuffer) > 0 {
            return true
        }
        return CaptureAudioSampleConverter.pcmBuffer(from: sampleBuffer) != nil
    }

    private func startSessionIfNeeded(on writer: AVAssetWriter) {
        guard !hasStartedSession else { return }
        guard videoInput != nil, systemAudioInput != nil, micAudioInput != nil else { return }
        guard writer.startWriting() else {
            logSegment("startWriting failed: \(writer.error?.localizedDescription ?? "unknown")")
            return
        }
        writer.startSession(atSourceTime: .zero)
        hasStartedSession = true
    }

    private func retimestampVideo(_ sampleBuffer: CMSampleBuffer, base: CMTime) -> CMSampleBuffer? {
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let local = CMTimeSubtract(pts, base)
        let duration = CMSampleBufferGetDuration(sampleBuffer)
        let frameDuration = CMTime(value: 1, timescale: CMTimeScale(captureFPS))
        var timing = CMSampleTimingInfo(
            duration: duration.isValid && duration.seconds > 0 ? duration : frameDuration,
            presentationTimeStamp: local,
            decodeTimeStamp: .invalid
        )
        var output: CMSampleBuffer?
        let status = CMSampleBufferCreateCopyWithNewTiming(
            allocator: kCFAllocatorDefault,
            sampleBuffer: sampleBuffer,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleBufferOut: &output
        )
        guard status == noErr else { return nil }
        return output
    }

    private func retimestampAudio(_ sampleBuffer: CMSampleBuffer, base: CMTime) -> CMSampleBuffer? {
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        var local = CMTimeSubtract(pts, base)
        if CMTimeCompare(local, .zero) < 0 { local = .zero }
        let duration = CMSampleBufferGetDuration(sampleBuffer)
        var timing = CMSampleTimingInfo(
            duration: duration.isValid ? duration : CMTime(value: 1, timescale: 48_000),
            presentationTimeStamp: local,
            decodeTimeStamp: .invalid
        )
        var output: CMSampleBuffer?
        let status = CMSampleBufferCreateCopyWithNewTiming(
            allocator: kCFAllocatorDefault,
            sampleBuffer: sampleBuffer,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleBufferOut: &output
        )
        guard status == noErr else { return nil }
        return output
    }

    func pauseForClip(completion: SegmentCompletion?) {
        pausedForClip = true
        if writer != nil {
            finalizeSegment(completion: completion)
        } else {
            completion?(nil)
        }
    }

    func resumeAfterClip() {
        pausedForClip = false
        clipBoundaryWallTime = nil
        guard writer == nil, !isFinalizing else { return }
        openNewSegmentFile()
    }

    func finalizeSegment(completion: SegmentCompletion?) {
        guard let writer, let url = segmentURL, !isFinalizing else {
            completion?(nil)
            return
        }
        isFinalizing = true
        let startedAt = segmentStartedAt
        let framesWritten = frameIndex
        let hadSession = hasStartedSession
        pendingFinalizationURLs.insert(url)
        currentSegmentURL = nil

        videoInput?.markAsFinished()
        systemAudioInput?.markAsFinished()
        micAudioInput?.markAsFinished()
        self.writer = nil
        videoInput = nil
        systemAudioInput = nil
        micAudioInput = nil
        segmentURL = nil
        hasStartedSession = false

        guard hadSession, framesWritten > 0 else {
            pendingFinalizationURLs.remove(url)
            try? FileManager.default.removeItem(at: url)
            isFinalizing = false
            completion?(nil)
            return
        }

        writer.finishWriting {
            self.pendingFinalizationURLs.remove(url)
            let segment: RecordingSegment?
            if writer.status == .completed,
               Self.fileSize(at: url) > 500,
               ClipExporter.hasReadableVideoSync(at: url) {
                let audioTracks = AVURLAsset(url: url).tracks(withMediaType: .audio).count
                self.logSegment(
                    "Segment finished \(url.lastPathComponent) audioTracks=\(audioTracks) \(self.audioDiagnostics)"
                )
                let measured = ClipExporter.measuredDurationSync(at: url)
                    ?? max(Double(framesWritten) / Double(self.captureFPS), 0.1)
                segment = RecordingSegment(url: url, startTime: startedAt, duration: measured, frameCount: Int(framesWritten))
            } else {
                try? FileManager.default.removeItem(at: url)
                segment = nil
            }
            self.isFinalizing = false
            self.onSegmentFinished(segment)
            completion?(segment)
        }
    }

    func startNewSegment() {
        guard !isFinalizing else { return }
        if writer != nil {
            finalizeSegment(completion: nil)
        } else {
            openNewSegmentFile()
        }
    }

    private static func fileSize(at url: URL) -> Int64 {
        (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.int64Value ?? 0
    }
}

@MainActor
final class ScreenRecorder: NSObject, ObservableObject {
    static let shared = ScreenRecorder()

    @Published private(set) var isCapturing = false
    @Published private(set) var statusMessage = "Starting capture…"
    @Published private(set) var isClipping = false
    @Published private(set) var bufferedSeconds: TimeInterval = 0
    @Published private(set) var isBufferReady = false
    @Published private(set) var segmentCount: Int = 0
    @Published private(set) var lastClipDebugSummary: String = ""

    private var stream: SCStream?
    private var segments: [RecordingSegment] = []
    private let segmentDuration: TimeInterval = 5
    private let maxBufferDuration: TimeInterval = 60

    private var segmentWriter: SegmentWriter?
    private nonisolated(unsafe) var captureWriter: SegmentWriter?
    private var bufferTicker: Task<Void, Never>?
    private var lastBufferMaintenance = Date.distantPast
    private let bufferMaintenanceInterval: TimeInterval = 5
    private var activeCaptureFPS: Int = 30

    private let bufferDirectory: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
            .appendingPathComponent("Clippy/Buffer", isDirectory: true)
        try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        return base
    }()

    private let processingQueue = DispatchQueue(label: "com.clippy.recorder.sync", qos: .userInitiated)

    private override init() {
        super.init()
    }

    func logRecorder(_ message: String) {
        ClippyDebugLog.shared.log("Recorder", message)
    }

    func internalDebugState() -> String {
        let protected = protectedSegmentURLs()
        let validCount = segments.filter { isValidSegmentFile($0.url) }.count
        var lines: [String] = []
        lines.append("writtenFrames=\(segmentWriter?.writtenFrameCount ?? 0)")
        if let writer = segmentWriter {
            lines.append(writer.audioDiagnostics)
        }
        lines.append("bufferDir=\(bufferDirectory.path)")
        lines.append("segmentsInMemory=\(segments.count) validOnDisk=\(validCount) pendingProtected=\(protected.count)")
        for segment in segments.suffix(5) {
            let size = fileSize(at: segment.url)
            let valid = isValidSegmentFile(segment.url)
            lines.append("  - \(segment.url.lastPathComponent) dur=\(String(format: "%.1f", segment.duration))s size=\(size) valid=\(valid)")
        }
        if let files = try? FileManager.default.contentsOfDirectory(at: bufferDirectory, includingPropertiesForKeys: [.fileSizeKey]) {
            let segFiles = files.filter { $0.lastPathComponent.hasPrefix("seg_") }
            lines.append("segFilesOnDisk=\(segFiles.count)")
            for file in segFiles.suffix(5) {
                let size = (try? file.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
                lines.append("  - \(file.lastPathComponent) size=\(size)")
            }
        }
        return lines.joined(separator: "\n")
    }

    private func logBufferSnapshot(_ phase: String) {
        logRecorder("--- clip \(phase) ---")
        logRecorder(RecorderDiagnostics.snapshot(recorder: self).replacingOccurrences(of: "\n", with: " | "))
        logRecorder(internalDebugState().replacingOccurrences(of: "\n", with: " | "))
    }

    func requestScreenCaptureAccess() {
        if !CGPreflightScreenCaptureAccess() {
            statusMessage = "Allow Screen Recording for Clippy…"
            CGRequestScreenCaptureAccess()
        }
    }

    func restartCapture() async {
        await stopCapture()
        segments.removeAll()
        bufferedSeconds = 0
        isBufferReady = false
        segmentCount = 0
        purgeInvalidSegmentFilesOnDisk()
        await startCapture()
    }

    private func purgeInvalidSegmentFilesOnDisk() {
        guard let files = try? FileManager.default.contentsOfDirectory(at: bufferDirectory, includingPropertiesForKeys: nil) else {
            return
        }
        for file in files where file.lastPathComponent.hasPrefix("seg_") {
            if !isValidSegmentFile(file) {
                try? FileManager.default.removeItem(at: file)
            }
        }
    }

    func startCapture() async {
        guard !isCapturing else { return }

        requestScreenCaptureAccess()
        guard CGPreflightScreenCaptureAccess() else {
            statusMessage = "Enable Screen Recording for Clippy in System Settings"
            return
        }

        do {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
            let preferredID = AppSettings.shared.preferredDisplayID
            guard let display = DisplayManager.resolveDisplay(id: preferredID, from: content.displays) else {
                statusMessage = "No display found"
                return
            }

            let displayIndex = content.displays.firstIndex(where: { $0.displayID == display.displayID }) ?? 0
            let displayName = DisplayManager.name(for: display, index: displayIndex)

            let filter = SCContentFilter(display: display, excludingWindows: [])
            let captureSettings = AppSettings.shared
            let resolution = captureSettings.captureResolution
            let frameRate = captureSettings.captureFrameRate
            activeCaptureFPS = frameRate.rawValue

            let dimensions = resolution.dimensions(for: display)
            let config = SCStreamConfiguration()
            config.width = dimensions.width
            config.height = dimensions.height
            config.minimumFrameInterval = frameRate.minimumFrameInterval
            config.queueDepth = 8
            config.capturesAudio = true
            config.captureMicrophone = true
            config.sampleRate = 48_000
            config.channelCount = 2
            config.excludesCurrentProcessAudio = true
            config.showsCursor = true
            config.pixelFormat = kCVPixelFormatType_32BGRA
            config.scalesToFit = true

            if let micCaptureID = AudioDeviceManager.avCaptureMicrophoneID(
                forPreferredUID: captureSettings.preferredMicrophoneUID
            ) {
                config.microphoneCaptureDeviceID = micCaptureID
            }

            let stream = SCStream(filter: filter, configuration: config, delegate: self)

            let writer = SegmentWriter(
                directory: bufferDirectory,
                segmentDuration: segmentDuration,
                captureFPS: frameRate.rawValue,
                videoBitrate: resolution.videoBitrate
            ) { [weak self] segment in
                Task { @MainActor in
                    guard let self, let segment else { return }
                    if !self.segments.contains(where: { $0.url == segment.url }) {
                        self.handleSegmentFinished(segment)
                    }
                }
            }
            writer.onMicrophoneSample = { sampleBuffer in
                VoiceCommandListener.shared.ingestSharedCaptureMicrophoneSampleNonisolated(sampleBuffer)
            }
            segmentWriter = writer
            captureWriter = writer
            try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: CapturePipeline.writerQueue)
            try stream.addStreamOutput(self, type: .audio, sampleHandlerQueue: CapturePipeline.writerQueue)
            try stream.addStreamOutput(self, type: .microphone, sampleHandlerQueue: CapturePipeline.writerQueue)

            if !captureSettings.preferredAudioOutputUID.isEmpty {
                AudioDeviceManager.setSystemDefaultOutputDevice(uid: captureSettings.preferredAudioOutputUID)
            }

            logRecorder(
                "Capture \(dimensions.width)x\(dimensions.height) @ \(frameRate.rawValue)fps | " +
                "output=\(AudioDeviceManager.resolvedOutputDeviceName(for: captureSettings.preferredAudioOutputUID)) | " +
                "mic=\(AudioDeviceManager.resolvedDeviceName(for: captureSettings.preferredMicrophoneUID)) | " +
                "micCaptureID=\(config.microphoneCaptureDeviceID ?? "default")"
            )

            try await stream.startCapture()
            self.stream = stream
            isCapturing = true
            statusMessage = "Buffering \(displayName)…"
            startBufferTicker()
            await VoiceCommandListener.shared.enableSharedCaptureMicrophoneIfNeeded()
        } catch {
            logRecorder("startCapture failed: \(error.localizedDescription)")
            ClippyDebugLog.shared.logError("Recorder", error, context: "startCapture")
            statusMessage = "Capture failed: \(error.localizedDescription)"
            isCapturing = false
        }
    }

    func stopCapture() async {
        bufferTicker?.cancel()
        if let stream {
            try? await stream.stopCapture()
        }
        await finalizeLegacySegment()
        stream = nil
        segmentWriter = nil
        captureWriter = nil
        isCapturing = false
        statusMessage = "Capture stopped"
        updateBufferState()
    }

    struct ClipResult {
        let url: URL
        let duration: TimeInterval
    }

    func createClip(maxDuration: TimeInterval) async throws -> ClipResult {
        isClipping = true
        defer { isClipping = false }

        logBufferSnapshot("start")

        let clipBoundary = Date()
        processingQueue.sync { [weak self] in
            self?.segmentWriter?.setClipBoundary(wallTime: clipBoundary)
        }

        let boundarySegment = await pauseAndFinalizeAtClipBoundary()
        await waitForPendingFinalizations(timeout: 3.0)

        if let boundarySegment {
            let refreshed = refreshedSegment(boundarySegment)
            if fileSize(at: refreshed.url) > 500,
               !segments.contains(where: { $0.url == refreshed.url }) {
                segments.append(refreshed)
                segments.sort { $0.startTime < $1.startTime }
                pruneSegments()
            }
        }

        let sourceSegments = await playableSegmentsForClip(maxDuration: maxDuration)
        guard !sourceSegments.isEmpty else {
            let summary = buildClipFailureSummary(freshlyFinalized: boundarySegment, extra: "no playable segments in buffer")
            throw clipFailure("No recording buffer available yet — wait a few seconds for the buffer to fill.", summary: summary)
        }

        let availableDuration = sourceSegments.reduce(0) { $0 + $1.duration }
        let targetDuration = min(maxDuration, availableDuration)
        let exportURL = bufferDirectory.appendingPathComponent("export_\(UUID().uuidString).mov")

        logRecorder("Exporting \(sourceSegments.count) segment(s), available=\(String(format: "%.2f", availableDuration))s target=\(String(format: "%.2f", targetDuration))s")

        do {
            try await ClipExporter.export(segments: sourceSegments, trimTo: targetDuration, outputURL: exportURL)
        } catch {
            ClippyDebugLog.shared.logError("Recorder", error, context: "export")
            let summary = buildClipFailureSummary(freshlyFinalized: boundarySegment, exportError: error)
            throw clipFailure(error.localizedDescription, summary: summary)
        }

        guard await ClipExporter.isPlayableVideo(at: exportURL) else {
            let summary = buildClipFailureSummary(freshlyFinalized: boundarySegment, extra: "export file not playable")
            throw clipFailure("Export produced an unplayable clip — try again.", summary: summary)
        }

        let exportedDuration = await ClipExporter.measuredDuration(at: exportURL) ?? targetDuration
        let clipDuration = min(maxDuration, exportedDuration)

        resumeCaptureAfterClip()
        updateBufferState()

        lastClipDebugSummary = "Clip OK — \(String(format: "%.1f", clipDuration))s (target \(Int(maxDuration))s) from \(sourceSegments.count) segment(s)"
        logRecorder(lastClipDebugSummary)
        return ClipResult(url: exportURL, duration: clipDuration)
    }

    private func waitForPendingFinalizations(timeout: TimeInterval) async {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            let pending = processingQueue.sync { segmentWriter?.pendingFinalizationURLs.count ?? 0 }
            if pending == 0 { break }
            try? await Task.sleep(nanoseconds: 50_000_000)
        }
    }

    private func pauseAndFinalizeAtClipBoundary() async -> RecordingSegment? {
        await withCheckedContinuation { continuation in
            processingQueue.async { [weak self] in
                self?.segmentWriter?.pauseForClip { segment in
                    Task { @MainActor in
                        continuation.resume(returning: segment)
                    }
                } ?? continuation.resume(returning: nil)
            }
        }
    }

    private func resumeCaptureAfterClip() {
        processingQueue.async { [weak self] in
            self?.segmentWriter?.resumeAfterClip()
        }
        logRecorder("Resumed recording after clip attempt")
    }

    private func playableSegmentsForClip(maxDuration: TimeInterval) async -> [RecordingSegment] {
        var byURL = [URL: RecordingSegment]()
        for segment in segments {
            byURL[segment.url] = refreshedSegment(segment)
        }
        for segment in discoverSegmentsOnDisk() {
            byURL[segment.url] = refreshedSegment(segment)
        }

        var playable: [RecordingSegment] = []
        for segment in byURL.values.sorted(by: { $0.startTime < $1.startTime }) {
            guard isValidSegmentFile(segment.url) else {
                logRecorder("Skipping invalid segment for clip: \(segment.url.lastPathComponent)")
                continue
            }
            playable.append(refreshedSegment(segment))
        }

        let selected = segmentsForDuration(maxDuration, from: playable)
        logRecorder("Clip segment pick: \(playable.count) playable → \(selected.count) selected, durations=\(selected.map { String(format: "%.1f", $0.duration) }.joined(separator: "+"))")
        return selected
    }

    private func refreshedSegment(_ segment: RecordingSegment) -> RecordingSegment {
        guard let measured = measuredDuration(for: segment.url), measured > 0.01 else { return segment }
        return RecordingSegment(
            url: segment.url,
            startTime: segment.startTime,
            duration: measured,
            frameCount: segment.frameCount
        )
    }

    private func buildClipFailureSummary(freshlyFinalized: RecordingSegment?, exportError: Error? = nil, extra: String? = nil) -> String {
        var lines = [RecorderDiagnostics.snapshot(recorder: self), internalDebugState()]
        if let freshlyFinalized {
            lines.append("freshSegment: \(freshlyFinalized.url.lastPathComponent) dur=\(freshlyFinalized.duration) size=\(fileSize(at: freshlyFinalized.url))")
        } else {
            lines.append("freshSegment: nil")
        }
        if let exportError { lines.append("exportError: \(exportError.localizedDescription)") }
        if let extra { lines.append(extra) }
        return lines.joined(separator: "\n")
    }

    private func resumeRecordingAfterClipAttempt() {
        resumeCaptureAfterClip()
    }

    func ingestSegment(_ segment: RecordingSegment) {
        guard !segments.contains(where: { $0.url == segment.url }) else { return }
        handleSegmentFinished(segment)
    }

    private func handleSegmentFinished(_ segment: RecordingSegment) {
        let segment = refreshedSegment(segment)
        guard ClipExporter.isValidSegmentFile(at: segment.url) else {
            logRecorder("Ignoring invalid segment: \(segment.url.lastPathComponent) size=\(fileSize(at: segment.url))")
            try? FileManager.default.removeItem(at: segment.url)
            return
        }
        segments.append(segment)
        logRecorder(
            "Segment ingested \(segment.url.lastPathComponent) dur=\(String(format: "%.1f", segment.duration))s " +
            (processingQueue.sync { segmentWriter?.audioDiagnostics } ?? "")
        )
        pruneSegments()
        updateBufferState()
    }

    private func startBufferTicker() {
        bufferTicker?.cancel()
        bufferTicker = Task { @MainActor in
            while !Task.isCancelled, isCapturing {
                maintainBufferIfNeeded()
                updateBufferState()
                try? await Task.sleep(nanoseconds: 1_000_000_000)
            }
        }
    }

    private func maintainBufferIfNeeded() {
        let now = Date()
        guard now.timeIntervalSince(lastBufferMaintenance) >= bufferMaintenanceInterval else { return }
        lastBufferMaintenance = now
        pruneSegments()
        purgeOrphanBufferFiles()
    }

    private func finalizeLegacySegment() async {
        let finished: RecordingSegment? = await withCheckedContinuation { continuation in
            processingQueue.async { [weak self] in
                self?.segmentWriter?.finalizeSegment { segment in
                    continuation.resume(returning: segment)
                } ?? continuation.resume(returning: nil)
            }
        }
        if let finished { ingestSegment(finished) }
        try? await Task.sleep(nanoseconds: 200_000_000)
    }

    private func validSegmentsForClip(maxDuration: TimeInterval) -> [RecordingSegment] {
        var byURL = [URL: RecordingSegment]()
        for segment in segments where isValidSegmentFile(segment.url) {
            byURL[segment.url] = segment
        }
        for segment in discoverSegmentsOnDisk() where isValidSegmentFile(segment.url) {
            byURL[segment.url] = segment
        }
        let merged = byURL.values.sorted { $0.startTime < $1.startTime }
        return segmentsForDuration(maxDuration, from: merged)
    }

    private func segmentsForDuration(_ duration: TimeInterval, from source: [RecordingSegment]) -> [RecordingSegment] {
        guard !source.isEmpty else { return [] }
        var total: TimeInterval = 0
        var selected: [RecordingSegment] = []
        for segment in source.reversed() {
            let refreshed = refreshedSegment(segment)
            selected.insert(refreshed, at: 0)
            total += refreshed.duration
            if total >= duration - 0.05 { break }
        }
        return selected
    }

    private func isValidSegmentFile(_ url: URL) -> Bool {
        ClipExporter.isValidSegmentFile(at: url)
    }

    private func clipFailure(_ userMessage: String, summary: String) -> ScreenRecorderError {
        lastClipDebugSummary = summary
        logRecorder("CLIP FAILED — \(userMessage)\n\(summary)")
        resumeCaptureAfterClip()
        updateBufferState()
        return .exportFailed(userMessage)
    }

    private func fileSize(at url: URL) -> Int64 {
        (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.int64Value ?? 0
    }

    private func discoverSegmentsOnDisk() -> [RecordingSegment] {
        guard let files = try? FileManager.default.contentsOfDirectory(
            at: bufferDirectory,
            includingPropertiesForKeys: [.contentModificationDateKey, .fileSizeKey]
        ) else { return [] }

        return files
            .filter { ($0.pathExtension == "mov" || $0.pathExtension == "mp4") && $0.lastPathComponent.hasPrefix("seg_") }
            .compactMap { url -> RecordingSegment? in
                guard isValidSegmentFile(url) else { return nil }
                let values = try? url.resourceValues(forKeys: [.contentModificationDateKey])
                let modified = values?.contentModificationDate ?? Date()
                let duration = measuredDuration(for: url) ?? segmentDuration
                return RecordingSegment(url: url, startTime: modified, duration: duration, frameCount: 0)
            }
            .sorted { $0.startTime < $1.startTime }
    }

    private func measuredDuration(for url: URL) -> TimeInterval? {
        if let existing = segments.first(where: { $0.url == url }), existing.duration > 0 {
            return existing.duration
        }
        let asset = AVURLAsset(url: url)
        if asset.duration.isValid, asset.duration.seconds > 0.01 {
            return asset.duration.seconds
        }
        return nil
    }

    private func protectedSegmentURLs() -> Set<URL> {
        processingQueue.sync {
            var urls = Set<URL>()
            if let writer = segmentWriter {
                urls.formUnion(writer.pendingFinalizationURLs)
                if let current = writer.currentSegmentURL {
                    urls.insert(current)
                }
            }
            return urls
        }
    }

    private func purgeStaleSegments() {
        let protected = protectedSegmentURLs()
        let before = segments.count
        segments = segments.filter { segment in
            if protected.contains(segment.url) { return true }
            if segment.duration > 0, fileSize(at: segment.url) > 500 { return true }
            logRecorder("Dropping stale segment ref: \(segment.url.lastPathComponent) size=\(fileSize(at: segment.url))")
            try? FileManager.default.removeItem(at: segment.url)
            return false
        }
        if segments.count != before {
            logRecorder("Purged \(before - segments.count) stale segment ref(s)")
        }
    }

    private func updateBufferState() {
        let validSegments = segments
        let finalized = validSegments.reduce(0) { $0 + $1.duration }
        let inProgress: TimeInterval
        if let writer = segmentWriter, writer.currentSegmentURL != nil {
            inProgress = min(segmentDuration, Double(writer.writtenFrameCount) / Double(max(activeCaptureFPS, 1)))
        } else {
            inProgress = 0
        }
        bufferedSeconds = min(maxBufferDuration, finalized + inProgress)
        segmentCount = validSegments.count
        isBufferReady = finalized >= 3 || (validSegments.count >= 1 && finalized >= segmentDuration - 0.5)

        if isCapturing {
            if isBufferReady {
                statusMessage = "Ready · \(Int(bufferedSeconds))s buffered"
            } else {
                statusMessage = "Buffering… \(Int(bufferedSeconds))s"
            }
        }
    }

    private func pruneSegments() {
        guard !isClipping else { return }
        purgeStaleSegments()

        segments.sort { $0.startTime < $1.startTime }
        var keptDuration: TimeInterval = 0
        var kept: [RecordingSegment] = []
        for segment in segments.reversed() {
            kept.insert(segment, at: 0)
            keptDuration += segment.duration
            if keptDuration >= maxBufferDuration - 0.05 {
                break
            }
        }

        let protected = protectedSegmentURLs()
        let keptURLs = Set(kept.map(\.url)).union(protected)
        let removed = segments.filter { !keptURLs.contains($0.url) }
        if !removed.isEmpty {
            logRecorder("Pruning \(removed.count) segment(s) beyond \(Int(maxBufferDuration))s buffer")
        }
        segments = segments.filter { keptURLs.contains($0.url) }

        deleteBufferFiles { url, name in
            let isSegment = name.hasPrefix("seg_") && (url.pathExtension == "mov" || url.pathExtension == "mp4")
            return isSegment && !keptURLs.contains(url)
        }
        updateBufferState()
    }

    private func purgeOrphanBufferFiles() {
        let protected = protectedSegmentURLs()
        let keep = Set(segments.map(\.url)).union(protected)

        deleteBufferFiles { url, name in
            if name.contains(".sb-") {
                guard let sbRange = name.range(of: ".sb-") else { return false }
                let parentURL = bufferDirectory.appendingPathComponent(String(name[..<sbRange.lowerBound]))
                if keep.contains(parentURL) || protected.contains(parentURL) {
                    return false
                }
                return !FileManager.default.fileExists(atPath: parentURL.path)
            }
            if name.hasPrefix("export_") {
                guard let attrs = try? FileManager.default.attributesOfItem(atPath: url.path),
                      let modified = attrs[.modificationDate] as? Date else {
                    return false
                }
                return Date().timeIntervalSince(modified) > 120
            }
            let isSegment = name.hasPrefix("seg_") && (url.pathExtension == "mov" || url.pathExtension == "mp4")
            return isSegment && !keep.contains(url)
        }
    }

    private func deleteBufferFiles(where shouldDelete: (URL, String) -> Bool) {
        guard let files = try? FileManager.default.contentsOfDirectory(at: bufferDirectory, includingPropertiesForKeys: nil) else {
            return
        }
        for file in files {
            let name = file.lastPathComponent
            if shouldDelete(file, name) {
                try? FileManager.default.removeItem(at: file)
            }
        }
    }
}

extension ScreenRecorder: SCStreamDelegate {
    nonisolated func stream(_ stream: SCStream, didStopWithError error: Error) {
        Task { @MainActor in
            ClippyDebugLog.shared.logError("Recorder", error, context: "SCStream stopped")
            isCapturing = false
            statusMessage = "Capture stopped — check Screen Recording permission for Clippy"
            isBufferReady = segments.contains { isValidSegmentFile($0.url) }
        }
    }
}

extension ScreenRecorder: SCStreamOutput {
    nonisolated func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard sampleBuffer.isValid else { return }
        let writer = captureWriter
        switch type {
        case .screen:
            guard let copy = CapturePipeline.copySampleBuffer(sampleBuffer) else { return }
            writer?.processVideo(copy)
        case .audio:
            writer?.noteSystemAudioReceived()
            guard let copy = CapturePipeline.copySampleBuffer(sampleBuffer) else { return }
            writer?.processSystemAudio(copy)
        case .microphone:
            guard let copy = CapturePipeline.copySampleBuffer(sampleBuffer) else { return }
            writer?.processMicrophoneAudio(copy)
        @unknown default:
            break
        }
    }
}

import AppKit
