using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clippy.Models;

public sealed class AppSettings
{
    private static AppSettings? _instance;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Instance => _instance ??= Load();

    public ClipDuration ClipDuration { get; set; } = ClipDuration.Thirty;
    public HotkeyBinding Hotkey { get; set; } = HotkeyBinding.Default;
    public bool VoiceCommandsEnabled { get; set; }
    public bool SoundEnabled { get; set; } = true;
    public string PreferredMicrophoneId { get; set; } = "";
    public string PreferredAudioOutputId { get; set; } = "";
    public string PreferredDisplayId { get; set; } = "";
    public CaptureResolution CaptureResolution { get; set; } = CaptureResolution.P720;
    public CaptureFrameRate CaptureFrameRate { get; set; } = CaptureFrameRate.Fps30;
    public bool HasCompletedOnboarding { get; set; }

    public void Persist()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(ClipStorage.SettingsPath, json);
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(ClipStorage.SettingsPath))
            {
                var json = File.ReadAllText(ClipStorage.SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
        }

        return new AppSettings();
    }
}
