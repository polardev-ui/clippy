import AVFoundation
import CoreMedia
import Foundation

enum SegmentAudioConverter {
    private static let targetFormat = AVAudioFormat(
        commonFormat: .pcmFormatFloat32,
        sampleRate: 48_000,
        channels: 2,
        interleaved: true
    )!

    private static var converterCache: [String: AVAudioConverter] = [:]
    private static let cacheLock = NSLock()

    private static let mono48kFormat = AVAudioFormat(
        commonFormat: .pcmFormatFloat32,
        sampleRate: 48_000,
        channels: 1,
        interleaved: false
    )!

    static func normalizedPCM(from sampleBuffer: CMSampleBuffer) -> AVAudioPCMBuffer? {
        guard CMSampleBufferIsValid(sampleBuffer) else { return nil }
        if !CMSampleBufferDataIsReady(sampleBuffer) {
            _ = CMSampleBufferMakeDataReady(sampleBuffer)
        }
        guard var pcmBuffer = CaptureAudioSampleConverter.pcmBuffer(from: sampleBuffer) else {
            return nil
        }
        if pcmBuffer.frameLength == 0 {
            let numSamples = CMSampleBufferGetNumSamples(sampleBuffer)
            if numSamples > 0 {
                pcmBuffer.frameLength = AVAudioFrameCount(numSamples)
            }
        }
        guard pcmBuffer.frameLength > 0 else { return nil }
        return convertToTargetFormat(pcmBuffer)
    }

    static func mix(
        primary: AVAudioPCMBuffer,
        secondary: AVAudioPCMBuffer?,
        primaryGain: Float = 0.88,
        secondaryGain: Float = 1.35
    ) -> AVAudioPCMBuffer {
        guard let primaryNorm = convertToTargetFormat(primary) else { return primary }
        guard let secondary,
              secondary.frameLength > 0,
              let secondaryNorm = convertToTargetFormat(secondary),
              secondaryNorm.frameLength > 0,
              let primaryData = primaryNorm.floatChannelData?[0],
              let secondaryData = secondaryNorm.floatChannelData?[0] else {
            return primaryNorm
        }

        let frames = Int(primaryNorm.frameLength)
        let secondaryFrames = Int(secondaryNorm.frameLength)
        guard frames > 0,
              let output = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: AVAudioFrameCount(frames)),
              let outputData = output.floatChannelData?[0] else {
            return primaryNorm
        }

        output.frameLength = AVAudioFrameCount(frames)
        let channels = Int(targetFormat.channelCount)

        for frame in 0..<frames {
            let secondaryFrame: Int
            if secondaryFrames >= frames {
                secondaryFrame = (secondaryFrames - frames) + frame
            } else {
                let alignStart = frames - secondaryFrames
                secondaryFrame = min(secondaryFrames - 1, max(0, frame - alignStart))
            }
            let priBase = frame * channels
            let secBase = secondaryFrame * channels
            for channel in 0..<channels {
                let mixed = primaryData[priBase + channel] * primaryGain
                    + secondaryData[secBase + channel] * secondaryGain
                outputData[priBase + channel] = max(-1, min(1, mixed))
            }
        }
        return output
    }

    static func convertToTargetFormat(_ buffer: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
        if matchesTargetFormat(buffer.format) {
            return clonePCM(buffer)
        }

        if buffer.format.channelCount == 1 {
            return convertMonoToTargetStereo(buffer)
        }

        if buffer.format.channelCount == 2,
           !buffer.format.isInterleaved,
           abs(buffer.format.sampleRate - targetFormat.sampleRate) < 1 {
            return interleaveStereo(toFloatIfNeeded(buffer) ?? buffer)
        }

        if let floated = toFloatIfNeeded(buffer), !floated.format.isEqual(buffer.format) {
            return convertToTargetFormat(floated)
        }

        return resampleToTarget(buffer)
    }

    private static func convertMonoToTargetStereo(_ buffer: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
        guard buffer.frameLength > 0 else { return nil }
        guard let mono48k = MicRateConverter.convert(buffer), mono48k.frameLength > 0 else { return nil }
        return upmixMono48kToStereo(mono48k)
    }

    private static func upmixMono48kToStereo(_ mono: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
        let frames = Int(mono.frameLength)
        guard frames > 0,
              let output = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: AVAudioFrameCount(frames)),
              let dst = output.floatChannelData?[0] else {
            return nil
        }
        output.frameLength = AVAudioFrameCount(frames)

        if let src = mono.floatChannelData?[0] {
            for index in 0..<frames {
                let sample = src[index]
                dst[index * 2] = sample
                dst[index * 2 + 1] = sample
            }
            return output
        }

        guard let samples = readMonoFloatSamples(mono), samples.count >= frames else { return nil }
        for index in 0..<frames {
            let sample = samples[index]
            dst[index * 2] = sample
            dst[index * 2 + 1] = sample
        }
        return output
    }

    private static func readMonoFloatSamples(_ buffer: AVAudioPCMBuffer) -> [Float]? {
        let frames = Int(buffer.frameLength)
        guard frames > 0 else { return nil }

        if buffer.format.commonFormat == .pcmFormatFloat32, let src = buffer.floatChannelData?[0] {
            return Array(UnsafeBufferPointer(start: src, count: frames))
        }

        if buffer.format.commonFormat == .pcmFormatInt32, let src = buffer.int32ChannelData?[0] {
            return (0..<frames).map { Float(src[$0]) / Float(Int32.max) }
        }

        if buffer.format.commonFormat == .pcmFormatInt16, let src = buffer.int16ChannelData?[0] {
            return (0..<frames).map { Float(src[$0]) / 32_768.0 }
        }

        let abl = UnsafeMutableAudioBufferListPointer(buffer.mutableAudioBufferList)
        guard let first = abl.first, first.mDataByteSize >= MemoryLayout<Float>.size else { return nil }
        let availableFrames = Int(first.mDataByteSize) / MemoryLayout<Float>.size
        let count = min(frames, availableFrames)
        let pointer = first.mData!.assumingMemoryBound(to: Float.self)
        return Array(UnsafeBufferPointer(start: pointer, count: count))
    }

    static func makeSampleBuffer(
        from pcmBuffer: AVAudioPCMBuffer,
        presentationTime: CMTime,
        duration: CMTime
    ) -> CMSampleBuffer? {
        guard pcmBuffer.format.isInterleaved,
              let formatDescription = pcmBuffer.format.formatDescription,
              let dataPointer = pcmBuffer.floatChannelData?[0] else {
            return nil
        }

        let frameCount = Int(pcmBuffer.frameLength)
        guard frameCount > 0 else { return nil }

        let sampleDuration = duration.isValid && duration.seconds > 0
            ? duration
            : CMTime(value: Int64(frameCount), timescale: 48_000)

        var timing = CMSampleTimingInfo(
            duration: sampleDuration,
            presentationTimeStamp: presentationTime,
            decodeTimeStamp: .invalid
        )

        var blockBuffer: CMBlockBuffer?
        let bytesPerFrame = Int(pcmBuffer.format.streamDescription.pointee.mBytesPerFrame)
        let dataLength = frameCount * bytesPerFrame

        guard CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault,
            memoryBlock: nil,
            blockLength: dataLength,
            blockAllocator: kCFAllocatorDefault,
            customBlockSource: nil,
            offsetToData: 0,
            dataLength: dataLength,
            flags: kCMBlockBufferAssureMemoryNowFlag,
            blockBufferOut: &blockBuffer
        ) == kCMBlockBufferNoErr,
        let blockBuffer else {
            return nil
        }

        CMBlockBufferReplaceDataBytes(
            with: dataPointer,
            blockBuffer: blockBuffer,
            offsetIntoDestination: 0,
            dataLength: dataLength
        )

        var sampleBuffer: CMSampleBuffer?
        let status = CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault,
            dataBuffer: blockBuffer,
            formatDescription: formatDescription,
            sampleCount: frameCount,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleSizeEntryCount: 0,
            sampleSizeArray: nil,
            sampleBufferOut: &sampleBuffer
        )
        guard status == noErr else { return nil }
        return sampleBuffer
    }

    private static func matchesTargetFormat(_ format: AVAudioFormat) -> Bool {
        format.sampleRate == targetFormat.sampleRate
            && format.channelCount == targetFormat.channelCount
            && format.isInterleaved
            && format.commonFormat == .pcmFormatFloat32
    }

    private static func toFloatIfNeeded(_ buffer: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
        guard buffer.format.commonFormat != .pcmFormatFloat32 else { return buffer }

        guard let floatFormat = AVAudioFormat(
            commonFormat: .pcmFormatFloat32,
            sampleRate: buffer.format.sampleRate,
            channels: buffer.format.channelCount,
            interleaved: buffer.format.isInterleaved
        ) else {
            return nil
        }

        if buffer.format.commonFormat == .pcmFormatInt16 {
            guard let output = AVAudioPCMBuffer(pcmFormat: floatFormat, frameCapacity: buffer.frameLength) else {
                return nil
            }
            output.frameLength = buffer.frameLength
            let channels = Int(buffer.format.channelCount)
            let frames = Int(buffer.frameLength)
            if buffer.format.isInterleaved,
               let src = buffer.int16ChannelData?[0],
               let dst = output.floatChannelData?[0] {
                let count = frames * channels
                for index in 0..<count {
                    dst[index] = Float(src[index]) / 32_768.0
                }
                return output
            }
            for channel in 0..<channels {
                guard let src = buffer.int16ChannelData?[channel],
                      let dst = output.floatChannelData?[channel] else {
                    return convertBuffer(buffer, to: floatFormat)
                }
                for frame in 0..<frames {
                    dst[frame] = Float(src[frame]) / 32_768.0
                }
            }
            return output
        }

        return convertBuffer(buffer, to: floatFormat)
    }

    private static func interleaveStereo(_ buffer: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
        guard buffer.format.channelCount == 2,
              let left = buffer.floatChannelData?[0],
              let right = buffer.floatChannelData?[1],
              let output = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: buffer.frameLength),
              let dst = output.floatChannelData?[0] else {
            return nil
        }
        output.frameLength = buffer.frameLength
        let frames = Int(buffer.frameLength)
        for index in 0..<frames {
            dst[index * 2] = left[index]
            dst[index * 2 + 1] = right[index]
        }
        return output
    }

    private static func resampleToTarget(_ buffer: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
        convertBuffer(buffer, to: targetFormat)
    }

    private static func convertBuffer(_ buffer: AVAudioPCMBuffer, to outputFormat: AVAudioFormat) -> AVAudioPCMBuffer? {
        guard buffer.frameLength > 0 else { return nil }
        if matchesTargetFormat(buffer.format) {
            return clonePCM(buffer)
        }

        let key = "\(buffer.format.description)->\(outputFormat.description)"
        cacheLock.lock()
        let cached = converterCache[key]
        if cached == nil || cached?.inputFormat != buffer.format || cached?.outputFormat != outputFormat {
            if let created = AVAudioConverter(from: buffer.format, to: outputFormat) {
                created.sampleRateConverterQuality = .max
                converterCache[key] = created
            }
            if converterCache.count > 12 {
                converterCache.removeValue(forKey: converterCache.keys.first!)
            }
        }
        let converter = converterCache[key]
        cacheLock.unlock()

        guard let converter else { return nil }

        let ratio = outputFormat.sampleRate / buffer.format.sampleRate
        let outCapacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 1024
        guard let output = AVAudioPCMBuffer(pcmFormat: outputFormat, frameCapacity: outCapacity) else {
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

    private static func clonePCM(_ buffer: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
        guard let copy = AVAudioPCMBuffer(pcmFormat: buffer.format, frameCapacity: buffer.frameLength) else {
            return nil
        }
        copy.frameLength = buffer.frameLength
        let channels = Int(buffer.format.channelCount)
        let frames = Int(buffer.frameLength)
        if buffer.format.isInterleaved, let src = buffer.floatChannelData?[0], let dst = copy.floatChannelData?[0] {
            let count = frames * channels
            dst.update(from: src, count: count)
            return copy
        }
        for channel in 0..<channels {
            guard let src = buffer.floatChannelData?[channel], let dst = copy.floatChannelData?[channel] else {
                return nil
            }
            dst.update(from: src, count: frames)
        }
        return copy
    }
}

private enum MicRateConverter {
    private static let targetFormat = AVAudioFormat(
        commonFormat: .pcmFormatFloat32,
        sampleRate: 48_000,
        channels: 1,
        interleaved: false
    )!

    private static var converter: AVAudioConverter?
    private static var inputFormat: AVAudioFormat?
    private static let lock = NSLock()

    static func convert(_ buffer: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
        lock.lock()
        if converter == nil || inputFormat != buffer.format {
            converter = AVAudioConverter(from: buffer.format, to: targetFormat)
            converter?.sampleRateConverterQuality = .max
            inputFormat = buffer.format
        }
        let activeConverter = converter
        lock.unlock()

        guard let activeConverter else { return nil }

        let ratio = targetFormat.sampleRate / buffer.format.sampleRate
        let outCapacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 1024
        guard let output = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: outCapacity) else {
            return nil
        }

        var consumed = false
        var error: NSError?
        let status = activeConverter.convert(to: output, error: &error) { _, outStatus in
            if consumed {
                outStatus.pointee = .noDataNow
                return nil
            }
            consumed = true
            outStatus.pointee = .haveData
            return buffer
        }

        guard status != .error, error == nil, output.frameLength > 0 else { return nil }

        if abs(buffer.format.sampleRate - targetFormat.sampleRate) > 1 {
            let expected = Double(buffer.frameLength) * targetFormat.sampleRate / buffer.format.sampleRate
            let actual = Double(output.frameLength)
            if actual < expected * 0.9 {
                return nil
            }
        }

        return output
    }
}

private extension AVAudioFormat {
    var formatDescription: CMFormatDescription? {
        var description: CMFormatDescription?
        var asbd = streamDescription.pointee
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
    }
}
