using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clippy.Models;

public enum ClipDuration
{
    Fifteen = 15,
    Thirty = 30,
    Sixty = 60
}

public static class ClipDurationExtensions
{
    public static string Label(this ClipDuration d) => d switch
    {
        ClipDuration.Fifteen => "15 seconds",
        ClipDuration.Thirty => "30 seconds",
        ClipDuration.Sixty => "1 minute",
        _ => "30 seconds"
    };

    public static string ShortLabel(this ClipDuration d) => d switch
    {
        ClipDuration.Fifteen => "15s",
        ClipDuration.Thirty => "30s",
        ClipDuration.Sixty => "1m",
        _ => "30s"
    };

    public static double Seconds(this ClipDuration d) => (int)d;

    public static IEnumerable<ClipDuration> All => new[] { ClipDuration.Fifteen, ClipDuration.Thirty, ClipDuration.Sixty };
}

public sealed class Clip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public double Duration { get; set; }
    public string FileName { get; set; } = "";
    public string Title { get; set; } = "";

    [JsonIgnore]
    public string FilePath => Path.Combine(ClipStorage.LibraryDirectory, FileName);

    public static string DefaultTitle(DateTime date) =>
        $"Clip · {date.ToLocalTime():g}";
}

public sealed class HotkeyBinding
{
    public uint VirtualKey { get; set; } = 0x4B;
    public uint Modifiers { get; set; } = (uint)HotkeyModifiers.Control;

    public static HotkeyBinding Default => new();

    public string DisplayString
    {
        get
        {
            var parts = new List<string>();
            var mods = (HotkeyModifiers)Modifiers;
            if (mods.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (mods.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (mods.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
            parts.Add(VirtualKeyToString(VirtualKey));
            return string.Join("+", parts);
        }
    }

    private static string VirtualKeyToString(uint vk) => vk switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Esc",
        0x20 => "Space",
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        _ => $"Key {vk}"
    };
}

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8
}

public enum CaptureResolution
{
    P360 = 360,
    P720 = 720,
    P1080 = 1080,
    P1440 = 1440
}

public static class CaptureResolutionExtensions
{
    public static string Label(this CaptureResolution r) => $"{(int)r}p";

    public static int VideoBitrate(this CaptureResolution r) => r switch
    {
        CaptureResolution.P360 => 800_000,
        CaptureResolution.P720 => 2_000_000,
        CaptureResolution.P1080 => 4_000_000,
        CaptureResolution.P1440 => 6_000_000,
        _ => 2_000_000
    };

    public static (int Width, int Height) DimensionsFor(CaptureResolution resolution, int displayWidth, int displayHeight)
    {
        var scale = Math.Min(1.0, (double)(int)resolution / displayHeight);
        var width = Math.Max(2, (int)(displayWidth * scale) & ~1);
        var height = Math.Max(2, (int)(displayHeight * scale) & ~1);
        return (width, height);
    }

    public static IEnumerable<CaptureResolution> All => new[]
    {
        CaptureResolution.P360,
        CaptureResolution.P720,
        CaptureResolution.P1080,
        CaptureResolution.P1440
    };
}

public enum CaptureFrameRate
{
    Fps15 = 15,
    Fps30 = 30,
    Fps60 = 60,
    Fps120 = 120
}

public static class CaptureFrameRateExtensions
{
    public static string Label(this CaptureFrameRate r) => $"{(int)r} fps";
    public static IEnumerable<CaptureFrameRate> All => new[]
    {
        CaptureFrameRate.Fps15,
        CaptureFrameRate.Fps30,
        CaptureFrameRate.Fps60,
        CaptureFrameRate.Fps120
    };
}

public sealed class CaptureDisplay
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

public sealed class AudioDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}

public sealed class RecordingSegment
{
    public required string Path { get; init; }
    public DateTime StartTime { get; init; }
    public double Duration { get; init; }
    public int FrameCount { get; init; }
}

public sealed class ClipExportResult
{
    public required string Path { get; init; }
    public double Duration { get; init; }
}

public static class ClipStorage
{
    public static string AppDataDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Clippy");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string LibraryDirectory
    {
        get
        {
            var dir = Path.Combine(AppDataDirectory, "Clips");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string BufferDirectory
    {
        get
        {
            var dir = Path.Combine(AppDataDirectory, "Buffer");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string IndexPath => Path.Combine(AppDataDirectory, "clips.json");
    public static string SettingsPath => Path.Combine(AppDataDirectory, "settings.json");
}
