import AVFoundation
import CoreMedia
import Foundation

private struct ExportSegmentInfo {
    let url: URL
    let duration: TimeInterval
    let videoTrack: AVAssetTrack
}

enum ClipExporter {
    enum ExportError: LocalizedError {
        case noVideo
        case exportFailed(String?)

        var errorDescription: String? {
            switch self {
            case .noVideo:
                return "Could not read video from the buffer — wait a few seconds and try again."
            case .exportFailed(let detail):
                if let detail, !detail.isEmpty { return detail }
                return "Failed to export the clip."
            }
        }
    }

    static func hasReadableVideoSync(at url: URL) -> Bool {
        guard fileSize(at: url) > 500 else { return false }
        let asset = AVURLAsset(url: url, options: [AVURLAssetPreferPreciseDurationAndTimingKey: true])
        guard asset.duration.isValid, asset.duration.seconds > 0.01 else { return false }
        guard let track = asset.tracks(withMediaType: .video).first else { return false }

        if canReadVideoSamplesSync(from: asset, track: track, outputSettings: nil) {
            return true
        }
        return canReadVideoSamplesSync(
            from: asset,
            track: track,
            outputSettings: [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA]
        )
    }

    static func isValidSegmentFile(at url: URL) -> Bool {
        hasReadableVideoSync(at: url)
    }

    static func isPlayableVideo(at url: URL) async -> Bool {
        guard fileSize(at: url) > 500 else { return false }
        let asset = AVURLAsset(url: url, options: [AVURLAssetPreferPreciseDurationAndTimingKey: true])
        guard (try? await asset.load(.isPlayable)) == true else { return false }
        guard let duration = try? await asset.load(.duration), duration.seconds > 0.01 else { return false }
        guard let track = try? await asset.loadTracks(withMediaType: .video).first else { return false }

        if canReadVideoSamplesSync(from: asset, track: track, outputSettings: nil) {
            return true
        }
        return canReadVideoSamplesSync(
            from: asset,
            track: track,
            outputSettings: [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA]
        )
    }

    static func isPlayableVideoSync(at url: URL) -> Bool {
        hasReadableVideoSync(at: url)
    }

    static func measuredDuration(at url: URL) async -> TimeInterval? {
        let asset = AVURLAsset(url: url, options: [AVURLAssetPreferPreciseDurationAndTimingKey: true])
        guard let duration = try? await asset.load(.duration), duration.isValid, duration.seconds > 0.01 else {
            return nil
        }
        return duration.seconds
    }

    static func measuredDurationSync(at url: URL) -> TimeInterval? {
        let asset = AVURLAsset(url: url, options: [AVURLAssetPreferPreciseDurationAndTimingKey: true])
        guard asset.duration.isValid, asset.duration.seconds > 0.01 else { return nil }
        return asset.duration.seconds
    }

    static func export(segments: [RecordingSegment], trimTo duration: TimeInterval, outputURL: URL) async throws {
        var valid: [RecordingSegment] = []
        for segment in segments where isValidSegmentFile(at: segment.url) {
            valid.append(segment)
        }
        guard !valid.isEmpty else { throw ExportError.noVideo }

        if FileManager.default.fileExists(atPath: outputURL.path) {
            try FileManager.default.removeItem(at: outputURL)
        }

        let attempts: [(String, () async throws -> Void)] = [
            ("composition", { try await exportWithComposition(segments: valid, trimTo: duration, outputURL: outputURL) }),
            ("passthrough", {
                try? FileManager.default.removeItem(at: outputURL)
                try await exportWithPassthrough(segments: valid, trimTo: duration, outputURL: outputURL)
            }),
            ("reencode", {
                try? FileManager.default.removeItem(at: outputURL)
                try await exportWithReencode(segments: valid, trimTo: duration, outputURL: outputURL)
            })
        ]

        var lastError: Error?
        let sourceHasAudio = await segmentsContainAudio(valid)
        for (name, attempt) in attempts {
            do {
                try await attempt()
                let exportHasAudio = await hasReadableAudio(at: outputURL)
                if FileManager.default.fileExists(atPath: outputURL.path),
                   fileSize(at: outputURL) > 500,
                   await isPlayableVideo(at: outputURL),
                   (!sourceHasAudio || exportHasAudio) {
                    return
                }
                lastError = ExportError.exportFailed(
                    "Export '\(name)' produced an unplayable file\(sourceHasAudio ? " (missing audio)" : "")."
                )
                try? FileManager.default.removeItem(at: outputURL)
            } catch {
                lastError = error
                try? FileManager.default.removeItem(at: outputURL)
            }
        }

        if let lastError {
            throw ExportError.exportFailed(friendlyMessage(from: lastError))
        }
        throw ExportError.noVideo
    }

    private static func exportWithComposition(
        segments: [RecordingSegment],
        trimTo duration: TimeInterval,
        outputURL: URL
    ) async throws {
        let composition = AVMutableComposition()
        guard let videoTrack = composition.addMutableTrack(
            withMediaType: .video,
            preferredTrackID: kCMPersistentTrackID_Invalid
        ) else {
            throw ExportError.noVideo
        }

        var systemAudioTrack: AVMutableCompositionTrack?
        var micAudioTrack: AVMutableCompositionTrack?
        var videoCursor = CMTime.zero
        var audioCursor = CMTime.zero

        for segment in segments {
            let asset = AVURLAsset(url: segment.url, options: [AVURLAssetPreferPreciseDurationAndTimingKey: true])
            let durationSeconds = await resolvedDuration(for: segment, asset: asset)
            guard durationSeconds > 0.01,
                  let sourceVideo = try await asset.loadTracks(withMediaType: .video).first else { continue }

            let transform = try await sourceVideo.load(.preferredTransform)
            videoTrack.preferredTransform = transform
            let range = CMTimeRange(
                start: .zero,
                duration: CMTime(seconds: durationSeconds, preferredTimescale: 60_000)
            )
            try videoTrack.insertTimeRange(range, of: sourceVideo, at: videoCursor)
            videoCursor = videoCursor + range.duration

            let audioTracks = try await asset.loadTracks(withMediaType: .audio)
            let sortedAudio = audioTracks.sorted { $0.trackID < $1.trackID }
            if let sourceSystem = sortedAudio.first {
                if systemAudioTrack == nil {
                    systemAudioTrack = composition.addMutableTrack(
                        withMediaType: .audio,
                        preferredTrackID: kCMPersistentTrackID_Invalid
                    )
                }
                if let systemAudioTrack {
                    try systemAudioTrack.insertTimeRange(range, of: sourceSystem, at: audioCursor)
                }
            }
            if sortedAudio.count >= 2 {
                if micAudioTrack == nil {
                    micAudioTrack = composition.addMutableTrack(
                        withMediaType: .audio,
                        preferredTrackID: kCMPersistentTrackID_Invalid
                    )
                }
                if let micAudioTrack {
                    try micAudioTrack.insertTimeRange(range, of: sortedAudio[1], at: audioCursor)
                }
            }
            audioCursor = audioCursor + range.duration
        }

        guard videoCursor.seconds > 0.01 else { throw ExportError.noVideo }

        let keepSeconds = min(duration, videoCursor.seconds)
        let startSeconds = max(0, videoCursor.seconds - keepSeconds)
        let timeRange = CMTimeRange(
            start: CMTime(seconds: startSeconds, preferredTimescale: 60_000),
            duration: CMTime(seconds: keepSeconds, preferredTimescale: 60_000)
        )

        let audioMix = makeExportAudioMix(systemTrack: systemAudioTrack, micTrack: micAudioTrack)

        for preset in [AVAssetExportPresetPassthrough, AVAssetExportPreset1280x720, AVAssetExportPresetHighestQuality] {
            guard let exporter = AVAssetExportSession(asset: composition, presetName: preset),
                  exporter.supportedFileTypes.contains(.mov) else { continue }
            exporter.outputURL = outputURL
            exporter.outputFileType = .mov
            exporter.timeRange = timeRange
            if audioMix != nil {
                exporter.audioMix = audioMix
            }
            await exporter.exportAsync()
            if exporter.status == .completed, fileSize(at: outputURL) > 500 { return }
            try? FileManager.default.removeItem(at: outputURL)
        }

        throw ExportError.exportFailed(nil)
    }

    private static func exportWithReencode(
        segments: [RecordingSegment],
        trimTo duration: TimeInterval,
        outputURL: URL
    ) async throws {
        var infos: [ExportSegmentInfo] = []
        for segment in segments {
            let asset = AVURLAsset(url: segment.url, options: [AVURLAssetPreferPreciseDurationAndTimingKey: true])
            let durationSeconds = await resolvedDuration(for: segment, asset: asset)
            guard durationSeconds > 0.01,
                  let video = try await asset.loadTracks(withMediaType: .video).first else { continue }
            infos.append(ExportSegmentInfo(url: segment.url, duration: durationSeconds, videoTrack: video))
        }

        guard !infos.isEmpty else { throw ExportError.noVideo }

        let totalDuration = infos.reduce(0) { $0 + $1.duration }
        let keepSeconds = min(duration, totalDuration)
        let skipSeconds = max(0, totalDuration - keepSeconds)

        var skipRemaining = skipSeconds
        var writeRemaining = keepSeconds

        let writer = try AVAssetWriter(outputURL: outputURL, fileType: .mov)
        writer.shouldOptimizeForNetworkUse = false

        var videoInput: AVAssetWriterInput?
        var sessionStarted = false
        var timeline: CMTime = .zero

        for info in infos {
            let segmentSkip = min(skipRemaining, info.duration)
            skipRemaining -= segmentSkip
            let segmentAvailable = info.duration - segmentSkip
            guard segmentAvailable > 0.01, writeRemaining > 0 else { continue }

            let segmentWrite = min(segmentAvailable, writeRemaining)
            writeRemaining -= segmentWrite

            let asset = AVURLAsset(url: info.url)
            let reader = try AVAssetReader(asset: asset)
            let timeRange = CMTimeRange(
                start: CMTime(seconds: segmentSkip, preferredTimescale: 60_000),
                duration: CMTime(seconds: segmentWrite, preferredTimescale: 60_000)
            )
            reader.timeRange = timeRange

            let videoOutput = AVAssetReaderTrackOutput(
                track: info.videoTrack,
                outputSettings: nil
            )
            videoOutput.alwaysCopiesSampleData = false
            guard reader.canAdd(videoOutput) else { continue }
            reader.add(videoOutput)
            guard reader.startReading() else {
                throw reader.error ?? ExportError.exportFailed("Could not read buffer segment.")
            }

            var segmentWritten: TimeInterval = 0
            while reader.status == .reading,
                  segmentWritten < segmentWrite - 0.001,
                  let sample = videoOutput.copyNextSampleBuffer() {
                if videoInput == nil {
                    let input = AVAssetWriterInput(mediaType: .video, outputSettings: nil)
                    input.expectsMediaDataInRealTime = false
                    let transform = try await info.videoTrack.load(.preferredTransform)
                    input.transform = transform
                    guard writer.canAdd(input) else { break }
                    writer.add(input)
                    videoInput = input
                }

                if !sessionStarted, let videoInput {
                    guard writer.startWriting() else {
                        throw writer.error ?? ExportError.exportFailed("Could not start export.")
                    }
                    writer.startSession(atSourceTime: timeline)
                    sessionStarted = true
                }

                while let videoInput, !videoInput.isReadyForMoreMediaData {
                    try await Task.sleep(nanoseconds: 2_000_000)
                }

                if let videoInput, videoInput.isReadyForMoreMediaData,
                   let retimed = retimestamp(sample, offset: timeline, clipStart: timeRange.start) {
                    videoInput.append(retimed)
                    let sampleDuration = CMSampleBufferGetDuration(sample)
                    segmentWritten += sampleDuration.isValid && sampleDuration.seconds > 0
                        ? sampleDuration.seconds
                        : (1.0 / 24.0)
                }
            }

            if reader.status == .failed {
                throw reader.error ?? ExportError.exportFailed("Buffer read failed.")
            }

            timeline = CMTimeAdd(timeline, CMTime(seconds: segmentWritten, preferredTimescale: 60_000))
        }

        guard sessionStarted, let videoInput else { throw ExportError.noVideo }

        videoInput.markAsFinished()
        try await finishWriting(writer)
    }

    private static func exportWithPassthrough(
        segments: [RecordingSegment],
        trimTo duration: TimeInterval,
        outputURL: URL
    ) async throws {
        struct SegmentInfo {
            let url: URL
            let duration: TimeInterval
            let videoTrack: AVAssetTrack
        }

        var infos: [SegmentInfo] = []
        for segment in segments {
            let asset = AVURLAsset(url: segment.url, options: [AVURLAssetPreferPreciseDurationAndTimingKey: true])
            let durationSeconds = await resolvedDuration(for: segment, asset: asset)
            guard durationSeconds > 0.01,
                  let video = try await asset.loadTracks(withMediaType: .video).first else { continue }
            infos.append(SegmentInfo(url: segment.url, duration: durationSeconds, videoTrack: video))
        }

        guard !infos.isEmpty else { throw ExportError.noVideo }

        let totalDuration = infos.reduce(0) { $0 + $1.duration }
        let keepSeconds = min(duration, totalDuration)
        let skipSeconds = max(0, totalDuration - keepSeconds)

        var skipRemaining = skipSeconds
        var writeRemaining = keepSeconds

        let writer = try AVAssetWriter(outputURL: outputURL, fileType: .mov)
        writer.shouldOptimizeForNetworkUse = false

        var videoInput: AVAssetWriterInput?
        var sessionStarted = false
        var timeline: CMTime = .zero

        for info in infos {
            let segmentSkip = min(skipRemaining, info.duration)
            skipRemaining -= segmentSkip
            let segmentAvailable = info.duration - segmentSkip
            guard segmentAvailable > 0.01, writeRemaining > 0 else { continue }

            let segmentWrite = min(segmentAvailable, writeRemaining)
            writeRemaining -= segmentWrite

            let asset = AVURLAsset(url: info.url)
            let reader = try AVAssetReader(asset: asset)
            let timeRange = CMTimeRange(
                start: CMTime(seconds: segmentSkip, preferredTimescale: 60_000),
                duration: CMTime(seconds: segmentWrite, preferredTimescale: 60_000)
            )
            reader.timeRange = timeRange

            let videoOutput = AVAssetReaderTrackOutput(track: info.videoTrack, outputSettings: nil)
            videoOutput.alwaysCopiesSampleData = false
            guard reader.canAdd(videoOutput) else { continue }
            reader.add(videoOutput)
            guard reader.startReading() else {
                throw reader.error ?? ExportError.exportFailed("Could not read buffer segment.")
            }

            var segmentWritten: TimeInterval = 0
            while reader.status == .reading,
                  segmentWritten < segmentWrite - 0.001,
                  let sample = videoOutput.copyNextSampleBuffer() {
                if videoInput == nil {
                    let input = AVAssetWriterInput(mediaType: .video, outputSettings: nil)
                    input.expectsMediaDataInRealTime = false
                    let transform = try await info.videoTrack.load(.preferredTransform)
                    input.transform = transform
                    guard writer.canAdd(input) else { break }
                    writer.add(input)
                    videoInput = input
                }

                if !sessionStarted, let videoInput {
                    guard writer.startWriting() else {
                        throw writer.error ?? ExportError.exportFailed("Could not start export.")
                    }
                    writer.startSession(atSourceTime: timeline)
                    sessionStarted = true
                }

                while let videoInput, !videoInput.isReadyForMoreMediaData {
                    try await Task.sleep(nanoseconds: 2_000_000)
                }

                if let videoInput, videoInput.isReadyForMoreMediaData,
                   let retimed = retimestamp(sample, offset: timeline, clipStart: timeRange.start) {
                    videoInput.append(retimed)
                    let sampleDuration = CMSampleBufferGetDuration(sample)
                    segmentWritten += sampleDuration.isValid && sampleDuration.seconds > 0
                        ? sampleDuration.seconds
                        : (1.0 / 24.0)
                }
            }

            if reader.status == .failed {
                throw reader.error ?? ExportError.exportFailed("Buffer read failed.")
            }

            timeline = CMTimeAdd(timeline, CMTime(seconds: segmentWritten, preferredTimescale: 60_000))
        }

        guard sessionStarted, let videoInput else { throw ExportError.noVideo }

        videoInput.markAsFinished()
        try await finishWriting(writer)
    }

    private static func makeExportAudioMix(
        systemTrack: AVMutableCompositionTrack?,
        micTrack: AVMutableCompositionTrack?
    ) -> AVAudioMix? {
        guard systemTrack != nil || micTrack != nil else { return nil }

        var parameters: [AVMutableAudioMixInputParameters] = []
        if let systemTrack {
            let params = AVMutableAudioMixInputParameters(track: systemTrack)
            params.setVolume(1.0, at: .zero)
            parameters.append(params)
        }
        if let micTrack {
            let params = AVMutableAudioMixInputParameters(track: micTrack)
            params.setVolume(1.25, at: .zero)
            parameters.append(params)
        }

        let mix = AVMutableAudioMix()
        mix.inputParameters = parameters
        return mix
    }

    private static func segmentsContainAudio(_ segments: [RecordingSegment]) async -> Bool {
        for segment in segments {
            let asset = AVURLAsset(url: segment.url, options: [AVURLAssetPreferPreciseDurationAndTimingKey: true])
            if let tracks = try? await asset.loadTracks(withMediaType: .audio), !tracks.isEmpty {
                return true
            }
        }
        return false
    }

    private static func hasReadableAudio(at url: URL) async -> Bool {
        let asset = AVURLAsset(url: url, options: [AVURLAssetPreferPreciseDurationAndTimingKey: true])
        guard let tracks = try? await asset.loadTracks(withMediaType: .audio), !tracks.isEmpty else {
            return false
        }
        return true
    }

    private static func resolvedDuration(for segment: RecordingSegment, asset: AVURLAsset) async -> TimeInterval {
        if let loaded = try? await asset.load(.duration), loaded.seconds > 0.01 {
            return loaded.seconds
        }
        if segment.duration > 0.01 {
            return segment.duration
        }
        return estimatedDurationFromFileSize(segment.url)
    }

    private static func estimatedDurationFromFileSize(_ url: URL) -> TimeInterval {
        let bytes = fileSize(at: url)
        guard bytes > 500 else { return 0 }
        return max(0.5, min(3.0, Double(bytes) / 200_000))
    }

    private static func canReadVideoSamplesSync(
        from asset: AVURLAsset,
        track: AVAssetTrack,
        outputSettings: [String: Any]?
    ) -> Bool {
        let reader = try? AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: outputSettings)
        guard let reader, reader.canAdd(output) else { return false }
        reader.add(output)
        guard reader.startReading() else { return false }
        defer { reader.cancelReading() }
        return output.copyNextSampleBuffer() != nil
    }

    private static func fileSize(at url: URL) -> Int64 {
        (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.int64Value ?? 0
    }

    private static func retimestamp(
        _ sample: CMSampleBuffer,
        offset: CMTime,
        clipStart: CMTime
    ) -> CMSampleBuffer? {
        let pts = CMSampleBufferGetPresentationTimeStamp(sample)
        let local = CMTimeSubtract(pts, clipStart)
        guard CMTimeCompare(local, .zero) >= 0 else { return nil }
        let newPTS = CMTimeAdd(offset, local)
        let duration = CMSampleBufferGetDuration(sample)
        var timing = CMSampleTimingInfo(
            duration: duration.isValid ? duration : CMTime(value: 1, timescale: 30),
            presentationTimeStamp: newPTS,
            decodeTimeStamp: .invalid
        )
        var output: CMSampleBuffer?
        let status = CMSampleBufferCreateCopyWithNewTiming(
            allocator: kCFAllocatorDefault,
            sampleBuffer: sample,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleBufferOut: &output
        )
        guard status == noErr else { return nil }
        return output
    }

    private static func finishWriting(_ writer: AVAssetWriter) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            writer.finishWriting {
                if writer.status == .completed {
                    continuation.resume()
                } else {
                    continuation.resume(throwing: writer.error ?? ExportError.exportFailed("Export writer failed."))
                }
            }
        }
    }

    private static func friendlyMessage(from error: Error) -> String {
        let ns = error as NSError
        let msg = ns.localizedDescription
        if msg == "The operation could not be completed" {
            return "Export failed — wait until the buffer shows Ready, then try again."
        }
        return msg
    }
}

private extension AVAssetExportSession {
    func exportAsync() async {
        await withCheckedContinuation { continuation in
            exportAsynchronously { continuation.resume() }
        }
    }
}
