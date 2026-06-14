using System.Runtime.InteropServices;
using Clippy.Models;

namespace Clippy.Services;

public static class DisplayManager
{
    public static IReadOnlyList<CaptureDisplay> RefreshDisplays()
    {
        var displays = new List<CaptureDisplay>();
        var index = 0;

        EnumDisplayMonitors(
            nint.Zero,
            nint.Zero,
            (hMonitor, _, lprcMonitor, _) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    var width = lprcMonitor.Right - lprcMonitor.Left;
                    var height = lprcMonitor.Bottom - lprcMonitor.Top;
                    var name = string.IsNullOrWhiteSpace(info.szDevice)
                        ? $"Display {index + 1}"
                        : info.szDevice.TrimEnd('\0');
                    displays.Add(new CaptureDisplay
                    {
                        Id = index.ToString(),
                        Label = $"{name} — {width}×{height}",
                        Width = width,
                        Height = height
                    });
                    index++;
                }

                return true;
            },
            nint.Zero);

        if (displays.Count == 0)
        {
            displays.Add(new CaptureDisplay
            {
                Id = "0",
                Label = "Primary Display",
                Width = 1920,
                Height = 1080
            });
        }

        return displays;
    }

    public static CaptureDisplay? DisplayById(string id, IReadOnlyList<CaptureDisplay> displays) =>
        displays.FirstOrDefault(d => d.Id == id) ?? displays.FirstOrDefault();

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
