import AVFoundation
import Foundation

@MainActor
final class SoundPlayer {
    static let shared = SoundPlayer()

    private var player: AVAudioPlayer?

    private init() {}

    func playClipSound() {
        guard AppSettings.shared.soundEnabled else { return }
        guard let url = Bundle.main.url(forResource: "clip", withExtension: "wav") else { return }
        do {
            player = try AVAudioPlayer(contentsOf: url)
            player?.volume = 0.85
            player?.prepareToPlay()
            player?.play()
        } catch {
        }
    }
}
