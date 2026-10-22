using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace CS2Soundboard.Core.Audio;

/// <summary>Snapshot of an audio endpoint, safe to pass to the UI thread.</summary>
public sealed record AudioDeviceInfo(string Id, string Name, string InterfaceName, bool IsCapture, bool IsActive)
{
    public bool LooksVirtual => VirtualDeviceRules.LooksVirtual(Name, InterfaceName);
    public bool IsVbCable => VirtualDeviceRules.IsVbCable(Name, InterfaceName);
    public override string ToString() => Name;
}

public static class VirtualDeviceRules
{
    private static readonly string[] VirtualKeywords =
    {
        "VB-Audio", "CABLE", "VoiceMeeter", "Virtual", "Voicemod", "Hi-Fi Cable", "BlackHole", "Steam Streaming",
    };

    public static bool LooksVirtual(string name, string interfaceName)
        => VirtualKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase) || interfaceName.Contains(k, StringComparison.OrdinalIgnoreCase));

    public static bool IsVbCable(string name, string interfaceName)
        => interfaceName.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase)
           || name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)
           || name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a capture endpoint is the recording side of the same (virtual) device as a render endpoint,
    /// e.g. "CABLE Output (VB-Audio Virtual Cable)" for "CABLE Input (VB-Audio Virtual Cable)".
    /// </summary>
    public static bool IsPair(AudioDeviceInfo render, AudioDeviceInfo capture)
        => !render.IsCapture && capture.IsCapture
           && !string.IsNullOrWhiteSpace(render.InterfaceName)
           && string.Equals(render.InterfaceName, capture.InterfaceName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Picks the recording endpoint that receives what is played into <paramref name="render"/>.
    /// Prefers the conventional name pair ("CABLE Input" -> "CABLE Output", "X In" -> "X Out"),
    /// then any endpoint of the same device.
    /// </summary>
    public static AudioDeviceInfo? FindRecordingSide(AudioDeviceInfo render, IEnumerable<AudioDeviceInfo> captures)
    {
        var candidates = captures.Where(c => IsPair(render, c)).ToList();
        if (candidates.Count <= 1) return candidates.FirstOrDefault();
        string[] expected =
        {
            render.Name.Replace("Input", "Output", StringComparison.OrdinalIgnoreCase),
            render.Name.Replace(" In ", " Out ", StringComparison.OrdinalIgnoreCase),
        };
        return candidates.FirstOrDefault(c => expected.Any(e => string.Equals(e, c.Name, StringComparison.OrdinalIgnoreCase)))
               ?? candidates.FirstOrDefault(c => c.Name.Contains("Output", StringComparison.OrdinalIgnoreCase))
               ?? candidates[0];
    }
}

public sealed record RoutingIssue(bool IsError, string Message);

/// <summary>Validates a device selection for feedback loops and misconfiguration.</summary>
public static class RoutingValidator
{
    public static List<RoutingIssue> Validate(AudioDeviceInfo? mic, AudioDeviceInfo? output, AudioDeviceInfo? localMonitor, IEnumerable<AudioDeviceInfo> captureDevices)
    {
        var issues = new List<RoutingIssue>();
        if (output == null)
        {
            issues.Add(new RoutingIssue(true, "No virtual output device selected. Select \"CABLE Input (VB-Audio Virtual Cable)\" (or another virtual cable's playback side)."));
        }
        else
        {
            if (!output.LooksVirtual)
                issues.Add(new RoutingIssue(false, $"\"{output.Name}\" does not look like a virtual audio cable. The mix (including your voice) will play on that device, and CS2 can only receive it through a matching virtual recording device."));

            if (mic != null && output.LooksVirtual)
            {
                var paired = captureDevices.Where(c => VirtualDeviceRules.IsPair(output, c)).ToList();
                if (paired.Any(p => p.Id == mic.Id))
                    issues.Add(new RoutingIssue(true, $"Feedback loop: \"{mic.Name}\" is the recording side of the virtual output \"{output.Name}\". Select your real (physical) microphone instead."));
            }
        }

        if (mic == null)
            issues.Add(new RoutingIssue(false, "No physical microphone selected. Only sound effects will be sent to CS2."));
        else if (mic.LooksVirtual && !issues.Any(i => i.IsError))
            issues.Add(new RoutingIssue(false, $"The selected microphone \"{mic.Name}\" looks like a virtual device. Normally you should select your physical microphone here."));

        if (localMonitor != null && output != null && localMonitor.Id == output.Id)
            issues.Add(new RoutingIssue(true, $"The local listening device is the same as the virtual output (\"{output.Name}\"). Previews and monitoring would be sent to CS2. Choose your speakers or headphones for local listening."));

        return issues;
    }
}

/// <summary>Enumerates endpoints and resolves saved selections. Use from the audio worker thread.</summary>
public sealed class DeviceCatalog : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private IMMNotificationClient? _client;

    public IReadOnlyList<AudioDeviceInfo> List(DataFlow flow)
    {
        var result = new List<AudioDeviceInfo>();
        foreach (var d in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            var info = ToInfo(d, flow);
            if (info != null) result.Add(info);
            d.Dispose();
        }
        return result.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static AudioDeviceInfo? ToInfo(MMDevice d, DataFlow flow)
    {
        try
        {
            string iface = "";
            try { iface = d.DeviceFriendlyName ?? ""; } catch { /* some drivers lack the property */ }
            return new AudioDeviceInfo(d.ID, d.FriendlyName, iface, flow == DataFlow.Capture, d.State == DeviceState.Active);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Finds an active endpoint by ID, falling back to an exact name match (e.g. after a driver reinstall).</summary>
    public MMDevice? FindActive(string? id, string? name, DataFlow flow)
    {
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                var d = _enumerator.GetDevice(id);
                if (d.State == DeviceState.Active && d.DataFlow == flow) return d;
                d.Dispose();
            }
            catch { /* not found */ }
        }
        if (!string.IsNullOrEmpty(name))
        {
            foreach (var d in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                if (string.Equals(d.FriendlyName, name, StringComparison.OrdinalIgnoreCase)) return d;
                d.Dispose();
            }
        }
        return null;
    }

    public AudioDeviceInfo? Describe(string? id, DataFlow flow)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            using var d = _enumerator.GetDevice(id);
            return ToInfo(d, flow);
        }
        catch { return null; }
    }

    public AudioDeviceInfo? GetDefault(DataFlow flow, Role role)
    {
        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(flow, role)) return null;
            using var d = _enumerator.GetDefaultAudioEndpoint(flow, role);
            return ToInfo(d, flow);
        }
        catch { return null; }
    }

    public MMDevice? GetDefaultDevice(DataFlow flow, Role role)
    {
        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(flow, role)) return null;
            return _enumerator.GetDefaultAudioEndpoint(flow, role);
        }
        catch { return null; }
    }

    public void RegisterNotifications(IMMNotificationClient client)
    {
        if (_client != null) _enumerator.UnregisterEndpointNotificationCallback(_client);
        _client = client;
        _enumerator.RegisterEndpointNotificationCallback(client);
    }

    public void Dispose()
    {
        try { if (_client != null) _enumerator.UnregisterEndpointNotificationCallback(_client); } catch { /* ignore */ }
        _client = null;
        _enumerator.Dispose();
    }
}

/// <summary>Forwards endpoint notifications as a single "devices changed" callback.</summary>
public sealed class DeviceChangeNotifier : IMMNotificationClient
{
    private readonly Action<string> _onChange;
    public DeviceChangeNotifier(Action<string> onChange) => _onChange = onChange;

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _onChange($"state:{deviceId}:{newState}");
    public void OnDeviceAdded(string pwstrDeviceId) => _onChange($"added:{pwstrDeviceId}");
    public void OnDeviceRemoved(string deviceId) => _onChange($"removed:{deviceId}");
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (role == Role.Multimedia) _onChange($"default:{flow}");
    }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { /* ignore: very chatty */ }
}
