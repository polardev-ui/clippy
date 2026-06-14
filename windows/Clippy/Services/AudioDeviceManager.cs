using System.Runtime.InteropServices;
using Clippy.Models;
using NAudio.CoreAudioApi;

namespace Clippy.Services;

public static class AudioDeviceManager
{
    public static IReadOnlyList<AudioDevice> InputDevices
    {
        get
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => new AudioDevice { Id = d.ID, Name = d.FriendlyName })
                .ToList();
        }
    }

    public static IReadOnlyList<AudioDevice> OutputDevices
    {
        get
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => new AudioDevice { Id = d.ID, Name = d.FriendlyName })
                .ToList();
        }
    }

    public static string ResolvedDeviceName(string id, DataFlow flow)
    {
        if (string.IsNullOrEmpty(id)) return "System Default";
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDevice(id).FriendlyName;
        }
        catch
        {
            return "Unknown Device";
        }
    }

    public static string ResolveInputId(string preferred)
    {
        if (string.IsNullOrEmpty(preferred)) return "";
        var devices = InputDevices;
        if (devices.Any(d => d.Id == preferred)) return preferred;
        var match = devices.FirstOrDefault(d =>
            d.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        return match?.Id ?? preferred;
    }

    public static string ResolveOutputId(string preferred)
    {
        if (string.IsNullOrEmpty(preferred)) return "";
        var devices = OutputDevices;
        if (devices.Any(d => d.Id == preferred)) return preferred;
        var match = devices.FirstOrDefault(d =>
            d.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        return match?.Id ?? preferred;
    }

    public static void SetDefaultOutputDevice(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        try
        {
            var policyConfig = (IPolicyConfig)new PolicyConfig();
            policyConfig.SetDefaultEndpoint(id, ERole.eMultimedia);
        }
        catch
        {
        }
    }

    public static void SetDefaultInputDevice(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        try
        {
            var policyConfig = (IPolicyConfig)new PolicyConfig();
            policyConfig.SetDefaultEndpoint(id, ERole.eCommunications);
        }
        catch
        {
        }
    }

    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfig
    {
    }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        void Unused1();
        void Unused2();
        void Unused3();
        void Unused4();
        void Unused5();
        void Unused6();
        void Unused7();
        void Unused8();
        void Unused9();
        void Unused10();
        void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    }

    private enum ERole
    {
        eConsole,
        eMultimedia,
        eCommunications
    }
}
