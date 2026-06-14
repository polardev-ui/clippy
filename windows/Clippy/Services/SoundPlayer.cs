using Clippy.Models;

namespace Clippy.Services;

public sealed class SoundPlayer
{
    private static SoundPlayer? _instance;
    public static SoundPlayer Instance => _instance ??= new SoundPlayer();

    public void PlayClipSound()
    {
        if (!AppSettings.Instance.SoundEnabled) return;

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
