import AVFoundation
import Foundation

enum ClipStorage {
    static var libraryDirectory: URL {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        let dir = appSupport.appendingPathComponent("Clippy/Clips", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    static var indexURL: URL {
        libraryDirectory.deletingLastPathComponent().appendingPathComponent("clips.json")
    }
}

@MainActor
final class ClipManager: ObservableObject {
    static let shared = ClipManager()

    @Published private(set) var clips: [Clip] = []

    private var indexURL: URL { ClipStorage.indexURL }

    private init() {
        loadClips()
    }

    func loadClips() {
        guard let data = try? Data(contentsOf: indexURL),
              let decoded = try? JSONDecoder().decode([Clip].self, from: data) else {
            clips = []
            return
        }
        clips = decoded.filter { FileManager.default.fileExists(atPath: $0.fileURL.path) }
            .sorted { $0.createdAt > $1.createdAt }
    }

    func addClip(from sourceURL: URL, duration: TimeInterval) async throws -> Clip {
        guard await ClipExporter.isPlayableVideo(at: sourceURL) else {
            throw ClipManagerError.unplayableExport
        }
        let fileName = "clip_\(UUID().uuidString).mov"
        let destination = ClipStorage.libraryDirectory.appendingPathComponent(fileName)
        if FileManager.default.fileExists(atPath: destination.path) {
            try FileManager.default.removeItem(at: destination)
        }
        try FileManager.default.copyItem(at: sourceURL, to: destination)
        guard await ClipExporter.isPlayableVideo(at: destination) else {
            try? FileManager.default.removeItem(at: destination)
            throw ClipManagerError.unplayableExport
        }
        let clip = Clip(duration: duration, fileName: fileName)
        clips.insert(clip, at: 0)
        saveIndex()
        return clip
    }

    enum ClipManagerError: LocalizedError {
        case unplayableExport

        var errorDescription: String? {
            "The exported clip could not be verified as playable video."
        }
    }

    func deleteClip(_ clip: Clip) {
        try? FileManager.default.removeItem(at: clip.fileURL)
        clips.removeAll { $0.id == clip.id }
        saveIndex()
    }

    func renameClip(_ clip: Clip, title: String) {
        guard let index = clips.firstIndex(where: { $0.id == clip.id }) else { return }
        clips[index].title = title
        saveIndex()
    }

    func revealInFinder(_ clip: Clip) {
        NSWorkspace.shared.activateFileViewerSelecting([clip.fileURL])
    }

    func exportClip(_ clip: Clip) {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.quickTimeMovie]
        panel.nameFieldStringValue = clip.fileName
        panel.begin { response in
            guard response == .OK, let url = panel.url else { return }
            try? FileManager.default.copyItem(at: clip.fileURL, to: url)
        }
    }

    private func saveIndex() {
        guard let data = try? JSONEncoder().encode(clips) else { return }
        try? data.write(to: indexURL, options: .atomic)
    }
}

import AppKit
