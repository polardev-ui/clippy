import SwiftUI

struct KeyRecorderView: View {
    @Binding var binding: HotkeyBinding

    @State private var isRecording = false
    @State private var pulse = false

    var body: some View {
        HStack(spacing: 14) {
            Text(isRecording ? "Press keys…" : binding.displayString)
                .font(.system(size: 24, weight: .bold, design: .rounded))
                .monospaced()
                .foregroundStyle(isRecording ? ClippyTheme.accent : ClippyTheme.textPrimary)
                .frame(minWidth: 120, alignment: .leading)
                .scaleEffect(isRecording && pulse ? 1.04 : 1)

            Spacer()

            Button(isRecording ? "Cancel" : "Change") {
                withAnimation(ClippyTheme.spring) {
                    isRecording.toggle()
                }
            }
            .buttonStyle(ClippySecondaryButtonStyle())

            Button("Reset") {
                withAnimation(ClippyTheme.spring) {
                    binding = .default
                }
            }
            .buttonStyle(ClippySecondaryButtonStyle())
        }
        .padding(16)
        .background(ClippyTheme.surfaceElevated)
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .stroke(isRecording ? ClippyTheme.accent : ClippyTheme.border, lineWidth: isRecording ? 2 : 1)
        )
        .background(
            KeyCaptureRepresentable(isRecording: $isRecording) { keyCode, modifiers in
                binding = HotkeyBinding(keyCode: keyCode, modifiers: modifiers)
                isRecording = false
            }
        )
        .onChange(of: isRecording) { _, recording in
            if recording {
                withAnimation(.easeInOut(duration: 0.8).repeatForever(autoreverses: true)) {
                    pulse = true
                }
            } else {
                pulse = false
            }
        }
    }
}

private struct KeyCaptureRepresentable: NSViewRepresentable {
    @Binding var isRecording: Bool
    var onCapture: (UInt16, UInt) -> Void

    func makeNSView(context: Context) -> KeyCaptureView {
        let view = KeyCaptureView()
        view.onCapture = onCapture
        return view
    }

    func updateNSView(_ nsView: KeyCaptureView, context: Context) {
        nsView.isRecording = isRecording
        if isRecording {
            DispatchQueue.main.async {
                nsView.window?.makeFirstResponder(nsView)
            }
        }
    }
}

final class KeyCaptureView: NSView {
    var isRecording = false
    var onCapture: ((UInt16, UInt) -> Void)?

    override var acceptsFirstResponder: Bool { true }

    override func keyDown(with event: NSEvent) {
        guard isRecording else {
            super.keyDown(with: event)
            return
        }
        if event.keyCode == 53 {
            isRecording = false
            return
        }
        let modifiers = event.modifierFlags.intersection([.command, .shift, .option, .control]).rawValue
        guard modifiers != 0 else { return }
        onCapture?(event.keyCode, modifiers)
    }
}

import AppKit
