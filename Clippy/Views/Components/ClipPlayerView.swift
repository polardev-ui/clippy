import AVFoundation
import AVKit
import SwiftUI

struct ClipPlayerView: NSViewRepresentable {
    let url: URL

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeNSView(context: Context) -> AVPlayerView {
        let view = AVPlayerView()
        view.controlsStyle = .inline
        view.showsFullScreenToggleButton = false
        context.coordinator.load(url: url, into: view)
        return view
    }

    func updateNSView(_ nsView: AVPlayerView, context: Context) {
        context.coordinator.load(url: url, into: nsView)
    }

    static func dismantleNSView(_ nsView: AVPlayerView, coordinator: Coordinator) {
        coordinator.cleanup(from: nsView)
    }

    final class Coordinator {
        private var player: AVPlayer?
        private var loadedURL: URL?

        func load(url: URL, into view: AVPlayerView) {
            guard loadedURL != url else { return }
            cleanup(from: view)
            let item = AVPlayerItem(url: url)
            let player = AVPlayer(playerItem: item)
            player.actionAtItemEnd = .pause
            self.player = player
            loadedURL = url
            view.player = player
            player.play()
        }

        func cleanup(from view: AVPlayerView) {
            player?.pause()
            view.player = nil
            player = nil
            loadedURL = nil
        }
    }
}
