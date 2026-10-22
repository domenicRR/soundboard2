using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CS2Soundboard.Core.Settings;

public enum PlaybackMode
{
    /// <summary>Several sounds may play at once (including copies of the same sound).</summary>
    Overlap,
    /// <summary>Triggering a sound stops every sound that is currently playing.</summary>
    StopPrevious,
    /// <summary>Sounds may overlap, but re-triggering a playing sound restarts it instead of adding a copy.</summary>
    NoDuplicates,
}

public enum MonitorMode
{
    Off,
    PhysicalMic,
    MixedOutput,
    VirtualRecording,
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    // Library
    public string? SoundDirectory { get; set; }
    public bool IncludeSubfolders { get; set; } = true;
    public bool AutoRescan { get; set; } = true;

    /// <summary>Relative sound path to serialized hotkey (e.g. "Ctrl+Alt+D1").</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? StopAllHotkey { get; set; } = "Ctrl+Alt+End";
    public string? ToggleMicMuteHotkey { get; set; }
    public string? ToggleSoundboardMuteHotkey { get; set; }

    // Devices (ID plus name as a fallback when the ID changes after a driver reinstall).
    public string? MicDeviceId { get; set; }
    public string? MicDeviceName { get; set; }
    public string? OutputDeviceId { get; set; }
    public string? OutputDeviceName { get; set; }
    /// <summary>Speakers/headphones for previews and monitoring. Null = Windows default playback device.</summary>
    public string? LocalDeviceId { get; set; }
    public string? LocalDeviceName { get; set; }
    public bool DevicesInitialized { get; set; }

    // Mixer
    public bool RoutingEnabled { get; set; } = true;
    public double MicGain { get; set; } = 1.0;
    public double SoundboardVolume { get; set; } = 0.8;
    public double MasterVolume { get; set; } = 1.0;
    public double LocalVolume { get; set; } = 0.8;
    public bool MicMuted { get; set; }
    public bool SoundboardMuted { get; set; }
    public int OutputLatencyMs { get; set; } = 30;

    // Playback
    public PlaybackMode PlaybackMode { get; set; } = PlaybackMode.Overlap;
    public bool RetriggerStops { get; set; }
    public bool HearSoundsLocally { get; set; } = true;

    // Window / startup
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; }
    public bool StartMinimized { get; set; }
    public bool StartWithWindows { get; set; }

    public void Normalize()
    {
        Hotkeys = new Dictionary<string, string>(Hotkeys ?? new(), StringComparer.OrdinalIgnoreCase);
        MicGain = Clamp(MicGain, 0, 4, 1);
        SoundboardVolume = Clamp(SoundboardVolume, 0, 2, 0.8);
        MasterVolume = Clamp(MasterVolume, 0, 2, 1);
        LocalVolume = Clamp(LocalVolume, 0, 2, 0.8);
        if (OutputLatencyMs < 10 || OutputLatencyMs > 200) OutputLatencyMs = 30;
        if (!Enum.IsDefined(PlaybackMode)) PlaybackMode = PlaybackMode.Overlap;
    }

    private static double Clamp(double v, double min, double max, double fallback)
        => double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
}

/// <summary>Loads and atomically saves settings as JSON in the user's roaming profile.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Keep "Ctrl+Alt+D1" readable in the file (no + escapes). The file is local, not HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();

    public SettingsStore(string? directory = null)
    {
        Directory = directory
            ?? Environment.GetEnvironmentVariable("CS2SB_SETTINGS_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CS2Soundboard");
        FilePath = Path.Combine(Directory, "settings.json");
    }

    public string Directory { get; }
    public string FilePath { get; }

    /// <summary>Loads settings. A corrupt file is backed up and replaced with defaults (warning returned).</summary>
    public (AppSettings Settings, string? Warning) Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return (new AppSettings(), null);
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                s.Normalize();
                return (s, null);
            }
            catch (Exception ex)
            {
                string? backup = null;
                try
                {
                    backup = Path.Combine(Directory, $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                    File.Copy(FilePath, backup, true);
                }
                catch { backup = null; }
                return (new AppSettings(),
                    $"Your settings file could not be read ({ex.Message}). Defaults were loaded." +
                    (backup != null ? $" The old file was saved as {backup}." : ""));
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
    }
}
