import SwiftUI

struct OnboardingView: View {
    @EnvironmentObject private var settings: AppSettings
    @EnvironmentObject private var coordinator: AppCoordinator
    @EnvironmentObject private var voice: VoiceCommandListener
    @ObservedObject private var audioDevices = AudioDeviceStore.shared

    @State private var step: Step = .welcome
    @State private var introPage = 0
    @State private var logoPulse = false
    @State private var contentVisible = false
    @State private var wavePhase = false

    private enum Step: Int, CaseIterable {
        case welcome
        case intro
        case microphone
        case output
        case voicePractice
    }

    var body: some View {
        ZStack {
            ClippyTheme.background.ignoresSafeArea()

            RadialGradient(
                colors: [ClippyTheme.accent.opacity(step == .voicePractice ? 0.16 : 0.1), .clear],
                center: .top,
                startRadius: 10,
                endRadius: 500
            )
            .ignoresSafeArea()
            .animation(ClippyTheme.easeOut, value: step)

            VStack(spacing: 0) {
                progressBar
                    .padding(.horizontal, 40)
                    .padding(.top, 28)

                Spacer(minLength: 12)

                Group {
                    switch step {
                    case .welcome: welcomeStep
                    case .intro: introStep
                    case .microphone: microphoneStep
                    case .output: outputStep
                    case .voicePractice: voicePracticeStep
                    }
                }
                .opacity(contentVisible ? 1 : 0)
                .offset(y: contentVisible ? 0 : 18)
                .animation(ClippyTheme.spring, value: contentVisible)
                .animation(ClippyTheme.spring, value: step)

                Spacer()

                footer
                    .padding(.horizontal, 40)
                    .padding(.bottom, 36)
            }
        }
        .frame(minWidth: 900, minHeight: 620)
        .onAppear {
            audioDevices.refreshDevices()
            animateIn()
            withAnimation(.easeInOut(duration: 1.8).repeatForever(autoreverses: true)) {
                logoPulse = true
            }
        }
        .onChange(of: step) { _, _ in
            contentVisible = false
            animateIn()
            handleStepChange()
        }
    }

    private var progressBar: some View {
        HStack(spacing: 8) {
            ForEach(0..<Step.allCases.count, id: \.self) { index in
                Capsule()
                    .fill(index <= step.rawValue ? ClippyTheme.accent : ClippyTheme.border)
                    .frame(height: 4)
                    .animation(ClippyTheme.spring, value: step)
            }
        }
    }

    private var welcomeStep: some View {
        VStack(spacing: 28) {
            logoBadge(size: 120)

            VStack(spacing: 12) {
                Text("Welcome to Clippy!")
                    .font(.system(size: 42, weight: .bold, design: .rounded))
                    .foregroundStyle(ClippyTheme.textPrimary)

                Text("Your instant replay button for Mac.")
                    .font(.title3)
                    .foregroundStyle(ClippyTheme.textSecondary)
            }
        }
        .padding(.horizontal, 40)
    }

    private var introStep: some View {
        VStack(spacing: 32) {
            logoBadge(size: 88)

            TabView(selection: $introPage) {
                introCard(
                    icon: "clock.arrow.circlepath",
                    title: "Always buffering",
                    body: "Clippy quietly records the last minute of your screen in the background — ready whenever you need it."
                )
                .tag(0)

                introCard(
                    icon: "film.stack",
                    title: "Clip in an instant",
                    body: "Save the last 15–60 seconds with a hotkey, button tap, or voice command. Perfect for bugs, demos, and \"wait, what just happened?\" moments."
                )
                .tag(1)

                introCard(
                    icon: "waveform.badge.mic",
                    title: "Just say the word",
                    body: "Try \"Clippy, clip that\" anytime. Clippy captures your screen, system audio, and microphone together."
                )
                .tag(2)
            }
            .tabViewStyle(.automatic)
            .frame(height: 280)

            HStack(spacing: 8) {
                ForEach(0..<3, id: \.self) { index in
                    Circle()
                        .fill(index == introPage ? ClippyTheme.accent : ClippyTheme.border)
                        .frame(width: 8, height: 8)
                }
            }
        }
        .padding(.horizontal, 48)
    }

    private func introCard(icon: String, title: String, body: String) -> some View {
        VStack(spacing: 18) {
            Image(systemName: icon)
                .font(.system(size: 44))
                .foregroundStyle(ClippyTheme.accent)
                .symbolEffect(.bounce, value: introPage)

            Text(title)
                .font(.title2.weight(.bold))
                .foregroundStyle(ClippyTheme.textPrimary)

            Text(body)
                .font(.body)
                .multilineTextAlignment(.center)
                .foregroundStyle(ClippyTheme.textSecondary)
                .frame(maxWidth: 480)
        }
        .padding(28)
        .clippyCard(highlighted: true)
    }

    private var microphoneStep: some View {
        deviceStep(
            title: "Choose your microphone",
            subtitle: "This is the mic Clippy uses for voice commands and clips.",
            icon: "mic.fill",
            devices: audioDevices.inputDevices.map { ($0.uid, $0.name) },
            selection: $settings.preferredMicrophoneUID
        )
    }

    private var outputStep: some View {
        deviceStep(
            title: "Choose your audio output",
            subtitle: "Clippy captures system audio from your Mac. Pick the speakers or headphones you're listening on.",
            icon: "speaker.wave.2.fill",
            devices: audioDevices.outputDevices.map { ($0.uid, $0.name) },
            selection: $settings.preferredAudioOutputUID
        )
    }

    private func deviceStep(
        title: String,
        subtitle: String,
        icon: String,
        devices: [(String, String)],
        selection: Binding<String>
    ) -> some View {
        VStack(spacing: 24) {
            Image(systemName: icon)
                .font(.system(size: 48))
                .foregroundStyle(ClippyTheme.accent)

            VStack(spacing: 10) {
                Text(title)
                    .font(.title.weight(.bold))
                    .foregroundStyle(ClippyTheme.textPrimary)
                Text(subtitle)
                    .font(.body)
                    .multilineTextAlignment(.center)
                    .foregroundStyle(ClippyTheme.textSecondary)
                    .frame(maxWidth: 520)
            }

            Picker(title, selection: selection) {
                ForEach(devices, id: \.0) { uid, name in
                    Text(name).tag(uid)
                }
            }
            .pickerStyle(.menu)
            .frame(width: 360)
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            .background(ClippyTheme.surfaceElevated)
            .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .stroke(ClippyTheme.border)
            )
        }
        .padding(.horizontal, 40)
    }

    private var voicePracticeStep: some View {
        VStack(spacing: 28) {
            logoBadge(size: 96)

            VStack(spacing: 14) {
                Text("Onboarding complete!")
                    .font(.system(size: 34, weight: .bold, design: .rounded))
                    .foregroundStyle(ClippyTheme.textPrimary)

                Text("Welcome to Clippy. To begin, just say:")
                    .font(.title3)
                    .foregroundStyle(ClippyTheme.textSecondary)

                Text("“Clippy, clip that”")
                    .font(.system(size: 28, weight: .semibold, design: .rounded))
                    .foregroundStyle(ClippyTheme.accent)
                    .padding(.horizontal, 24)
                    .padding(.vertical, 14)
                    .background(ClippyTheme.accent.opacity(0.12))
                    .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
            }

            HStack(spacing: 12) {
                Image(systemName: "waveform")
                    .font(.title2)
                    .foregroundStyle(ClippyTheme.accent)
                    .symbolEffect(.variableColor.iterative.reversing, isActive: voice.isListening)

                Text(voice.isListening ? "Listening…" : "Starting voice…")
                    .font(.headline)
                    .foregroundStyle(ClippyTheme.textSecondary)
            }
            .padding(.top, 8)

            if let heard = voice.lastHeardPhrase, !heard.isEmpty {
                Text("Heard: \"\(heard)\"")
                    .font(.caption)
                    .foregroundStyle(ClippyTheme.textSecondary)
            }
        }
        .padding(.horizontal, 40)
        .onAppear {
            coordinator.beginOnboardingVoicePractice()
        }
    }

    private func logoBadge(size: CGFloat) -> some View {
        ZStack {
            Circle()
                .fill(ClippyTheme.accent.opacity(0.14))
                .frame(width: size + 16, height: size + 16)
                .scaleEffect(logoPulse ? 1.08 : 0.94)

            Image("ClippyLogo")
                .resizable()
                .scaledToFill()
                .frame(width: size, height: size)
                .clipShape(Circle())
                .overlay(Circle().stroke(ClippyTheme.accent.opacity(0.35), lineWidth: 2))
        }
        .clippyGlow(isActive: step == .voicePractice)
    }

    private var footer: some View {
        HStack {
            if step != .welcome && step != .voicePractice {
                Button("Back") { goBack() }
                    .buttonStyle(.plain)
                    .foregroundStyle(ClippyTheme.textSecondary)
            }

            Spacer()

            if step != .voicePractice {
                Button(step == .output ? "Continue" : "Next") { goForward() }
                    .buttonStyle(.plain)
                    .font(.headline)
                    .padding(.horizontal, 24)
                    .padding(.vertical, 12)
                    .background(ClippyTheme.accent)
                    .foregroundStyle(.black)
                    .clipShape(Capsule())
            } else {
                Button("Skip for now") {
                    coordinator.completeOnboarding(fromVoiceDemo: false)
                }
                .buttonStyle(.plain)
                .foregroundStyle(ClippyTheme.textSecondary)
            }
        }
    }

    private func goForward() {
        switch step {
        case .welcome:
            step = .intro
        case .intro:
            if introPage < 2 {
                withAnimation(ClippyTheme.spring) { introPage += 1 }
            } else {
                step = .microphone
            }
        case .microphone:
            step = .output
        case .output:
            step = .voicePractice
        case .voicePractice:
            break
        }
    }

    private func goBack() {
        switch step {
        case .intro:
            if introPage > 0 {
                withAnimation(ClippyTheme.spring) { introPage -= 1 }
            } else {
                step = .welcome
            }
        case .microphone:
            step = .intro
            introPage = 2
        case .output:
            step = .microphone
        default:
            break
        }
    }

    private func animateIn() {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
            contentVisible = true
        }
    }

    private func handleStepChange() {
        if step == .output {
            coordinator.applyOnboardingAudioDevices()
        }
    }
}
