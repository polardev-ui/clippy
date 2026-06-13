import SwiftUI

@main
struct ClippyApp: App {
    @StateObject private var settings = AppSettings.shared
    @StateObject private var coordinator = AppCoordinator.shared
    @StateObject private var clipManager = ClipManager.shared
    @StateObject private var recorder = ScreenRecorder.shared
    @StateObject private var voice = VoiceCommandListener.shared
    @StateObject private var debugLog = ClippyDebugLog.shared

    var body: some Scene {
        WindowGroup {
            Group {
                if coordinator.showOnboarding {
                    OnboardingView()
                } else {
                    ContentView()
                }
            }
                .environmentObject(settings)
                .environmentObject(coordinator)
                .environmentObject(clipManager)
                .environmentObject(recorder)
                .environmentObject(voice)
                .preferredColorScheme(.dark)
                .background(ClippyTheme.background)
                .onAppear {
                    coordinator.bootstrap()
                }
        }
        .defaultSize(width: 980, height: 680)
        .windowResizability(.contentMinSize)
    }
}
