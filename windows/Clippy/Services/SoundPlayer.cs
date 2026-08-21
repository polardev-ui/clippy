using Clippy.Models;

namespace Clippy.Services;

public sealed class SoundPlayer
{
    private static SoundPlayer? _instance;
    public static SoundPlayer Instance => _instance ??= new SoundPlayer();

    /// <summary>
    /// Fires and forgets. Playback runs off the UI thread — this is triggered from the
    /// global hotkey, which arrives on the window procedure, and blocking there would
    /// freeze the window for the length of the sound.
    /// </summary>
    public void PlayClipSound()
    {
        if (!AppSettings.Instance.SoundEnabled) return;

        _ = Task.Run(PlayBlocking);
    }

    private static void PlayBlocking()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "clip.wav");
            if (!File.Exists(path)) return;

            using var player = new NAudio.Wave.AudioFileReader(path);
            using var output = new NAudio.Wave.WaveOutEvent();
            output.Init(player);
            output.Volume = 0.85f;
            output.Play();
            while (output.PlaybackState == NAudio.Wave.PlaybackState.Playing)
            {
                Thread.Sleep(50);
            }
        }
        catch
        {
        }
    }
}
