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
        0x13 => "Pause",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "PgUp",
        0x22 => "PgDn",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2C => "PrtSc",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => $"Num{vk - 0x60}",
        0x6A => "Num*",
        0x6B => "Num+",
        0x6D => "Num-",
        0x6E => "Num.",
        0x6F => "Num/",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
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
    public int OffsetX { get; init; }
    public int OffsetY { get; init; }
}

public sealed class AudioDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}

public sealed class RecordingSegment
{
    public required string Path { get; init; }

    /// <summary>Position in the rolling buffer; segments concatenate in index order.</summary>
    public int Index { get; init; }

    public DateTime StartTime { get; init; }

    /// <summary>Nominal length. Zero for the segment FFmpeg is still writing.</summary>
    public double Duration { get; init; }

    /// <summary>False for the in-progress segment, which is still readable but not full length.</summary>
    public bool IsComplete { get; init; }
}

/// <summary>Everything the recorder needs to start a capture, resolved from user settings.</summary>
public sealed class CaptureSettings
{
    public int OffsetX { get; init; }
    public int OffsetY { get; init; }

    /// <summary>Native display size — what gdigrab actually grabs.</summary>
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }

    /// <summary>Encoded size after scaling, from the chosen quality setting.</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    public int FrameRate { get; init; } = 30;
    public int VideoBitrate { get; init; } = 2_000_000;
    public bool CaptureMicrophone { get; init; } = true;
    public string? MicrophoneDeviceId { get; init; }
    public string? OutputDeviceId { get; init; }

    public static CaptureSettings FromAppSettings(CaptureDisplay display)
    {
        var settings = AppSettings.Instance;
        var (width, height) = CaptureResolutionExtensions.DimensionsFor(
            settings.CaptureResolution, display.Width, display.Height);

        return new CaptureSettings
        {
            OffsetX = display.OffsetX,
            OffsetY = display.OffsetY,
            SourceWidth = EvenDimension(display.Width),
            SourceHeight = EvenDimension(display.Height),
            Width = width,
            Height = height,
            FrameRate = (int)settings.CaptureFrameRate,
            VideoBitrate = settings.CaptureResolution.VideoBitrate(),
            CaptureMicrophone = true,
            MicrophoneDeviceId = string.IsNullOrEmpty(settings.PreferredMicrophoneId)
                ? null
                : settings.PreferredMicrophoneId,
            OutputDeviceId = string.IsNullOrEmpty(settings.PreferredAudioOutputId)
                ? null
                : settings.PreferredAudioOutputId
        };
    }

    // libx264's yuv420p needs even dimensions in both axes.
    private static int EvenDimension(int value) => Math.Max(2, value & ~1);
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
