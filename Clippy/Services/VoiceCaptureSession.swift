import AVFoundation
import CoreMedia
import Foundation

final class VoiceAudioCapture {
    private let engine = AVAudioEngine()
    private let queue = DispatchQueue(label: "com.clippy.voice.audio", qos: .userInitiated)
    private var pcmHandler: ((AVAudioPCMBuffer) -> Void)?

    func start(preferredUID: String, onBuffer: @escaping (AVAudioPCMBuffer) -> Void) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            queue.async {
                do {
                    try self.startLocked(preferredUID: preferredUID, onBuffer: onBuffer)
                    continuation.resume()
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        }
    }

    func stop() async {
        await withCheckedContinuation { continuation in
            queue.async {
                if self.engine.isRunning {
                    self.engine.stop()
                    self.engine.inputNode.removeTap(onBus: 0)
                }
                self.pcmHandler = nil
                continuation.resume()
            }
        }
    }

    private func startLocked(preferredUID: String, onBuffer: @escaping (AVAudioPCMBuffer) -> Void) throws {
        if engine.isRunning {
            engine.stop()
            engine.inputNode.removeTap(onBus: 0)
        }
        pcmHandler = onBuffer
        if !preferredUID.isEmpty {
            AudioDeviceManager.setSystemDefaultInputDevice(uid: preferredUID)
        }
        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        input.installTap(onBus: 0, bufferSize: 4096, format: format) { [weak self] buffer, _ in
            self?.pcmHandler?(buffer)
        }
        engine.prepare()
        try engine.start()
    }
}

enum CaptureAudioSampleConverter {
    static func pcmBuffer(from sampleBuffer: CMSampleBuffer) -> AVAudioPCMBuffer? {
        guard CMSampleBufferIsValid(sampleBuffer),
              let formatDescription = CMSampleBufferGetFormatDescription(sampleBuffer),
              let streamDescription = CMAudioFormatDescriptionGetStreamBasicDescription(formatDescription) else {
            return nil
        }

        if streamDescription.pointee.mFormatID != kAudioFormatLinearPCM {
            return nil
        }

        if !CMSampleBufferDataIsReady(sampleBuffer) {
            _ = CMSampleBufferMakeDataReady(sampleBuffer)
        }

        guard let format = AVAudioFormat(streamDescription: streamDescription) else {
            return nil
        }

        var frameCount = CMSampleBufferGetNumSamples(sampleBuffer)
        if frameCount <= 0 {
            let duration = CMSampleBufferGetDuration(sampleBuffer)
            if duration.isValid, duration.seconds > 0 {
                frameCount = max(1, Int(duration.seconds * streamDescription.pointee.mSampleRate))
            } else if CMSampleBufferGetTotalSampleSize(sampleBuffer) > 0 {
                let bytesPerFrame = max(1, Int(streamDescription.pointee.mBytesPerFrame))
                frameCount = CMSampleBufferGetTotalSampleSize(sampleBuffer) / bytesPerFrame
            }
        }
        guard frameCount > 0,
              let pcmBuffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frameCount)) else {
            return nil
        }
        pcmBuffer.frameLength = AVAudioFrameCount(frameCount)

        let status = CMSampleBufferCopyPCMDataIntoAudioBufferList(
            sampleBuffer,
            at: 0,
            frameCount: Int32(frameCount),
            into: pcmBuffer.mutableAudioBufferList
        )
        if status == noErr {
            return pcmBuffer
        }

        return pcmBufferFromAudioBufferList(sampleBuffer, format: format, frameCount: frameCount)
    }

    private static func pcmBufferFromAudioBufferList(
        _ sampleBuffer: CMSampleBuffer,
        format: AVAudioFormat,
        frameCount: Int
    ) -> AVAudioPCMBuffer? {
        guard let pcmBuffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frameCount)) else {
            return nil
        }
        pcmBuffer.frameLength = AVAudioFrameCount(frameCount)

        var blockBuffer: CMBlockBuffer?
        let status = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer,
            bufferListSizeNeededOut: nil,
            bufferListOut: pcmBuffer.mutableAudioBufferList,
            bufferListSize: MemoryLayout<AudioBufferList>.size,
            blockBufferAllocator: kCFAllocatorDefault,
            blockBufferMemoryAllocator: kCFAllocatorDefault,
            flags: 0,
            blockBufferOut: &blockBuffer
        )
        guard status == noErr else { return nil }
        return pcmBuffer
    }
}

final class SpeechAudioConverter {
    private let targetFormat = AVAudioFormat(
        commonFormat: .pcmFormatFloat32,
        sampleRate: 16_000,
        channels: 1,
        interleaved: false
    )!

    private var converter: AVAudioConverter?

    func convert(_ buffer: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
        if converter == nil || converter?.inputFormat != buffer.format {
            converter = AVAudioConverter(from: buffer.format, to: targetFormat)
        }
        guard let converter else { return nil }

        let ratio = targetFormat.sampleRate / buffer.format.sampleRate
        let outCapacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 1024
        guard let output = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: outCapacity) else {
            return nil
        }

        var consumed = false
        var error: NSError?
        let status = converter.convert(to: output, error: &error) { _, outStatus in
            if consumed {
                outStatus.pointee = .noDataNow
                return nil
            }
            consumed = true
            outStatus.pointee = .haveData
            return buffer
        }

        guard status != .error, error == nil, output.frameLength > 0 else { return nil }
        return output
    }
}
