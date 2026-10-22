using System;
using CS2Soundboard.Core.Hotkeys;

namespace CS2Soundboard.ViewModels;

/// <summary>Anything that can own a global hotkey (a sound or an app command).</summary>
public interface IHotkeyTarget
{
    string Binding { get; }
    string DisplayName { get; }
    Hotkey Hotkey { get; set; }
    string? HotkeyError { get; set; }
}

public enum SoundState { Loading, Ready, Error, Missing }

public sealed class SoundItemViewModel : ObservableObject, IHotkeyTarget
{
    private Hotkey _hotkey;
    private string? _hotkeyError;
    private SoundState _state = SoundState.Loading;
    private string? _errorMessage;
    private TimeSpan? _duration;
    private bool _isPlaying;
    private bool _isPreviewing;
    private double _progress;

    public SoundItemViewModel(string key, string fullPath, string name, string folder)
    {
        Key = key;
        FullPath = fullPath;
        Name = name;
        Folder = folder;
    }

    /// <summary>Relative path inside the sound folder; stable identifier for hotkeys.</summary>
    public string Key { get; }
    public string FullPath { get; }
    public string Name { get; }
    public string Folder { get; }
    public string Binding => "sound:" + Key;
    public string DisplayName => Name;
    public string Extension => System.IO.Path.GetExtension(FullPath).TrimStart('.').ToUpperInvariant();

    public Hotkey Hotkey
    {
        get => _hotkey;
        set
        {
            if (SetProperty(ref _hotkey, value))
            {
                OnPropertyChanged(nameof(HotkeyText));
                OnPropertyChanged(nameof(HasHotkey));
            }
        }
    }

    public string HotkeyText => _hotkey.IsEmpty ? "Set hotkey" : _hotkey.ToString();
    public bool HasHotkey => !_hotkey.IsEmpty;

    public string? HotkeyError
    {
        get => _hotkeyError;
        set { if (SetProperty(ref _hotkeyError, value)) OnPropertyChanged(nameof(HasHotkeyError)); }
    }

    public bool HasHotkeyError => !string.IsNullOrEmpty(_hotkeyError);

    public SoundState State
    {
        get => _state;
        set { if (SetProperty(ref _state, value)) RaiseStatus(); }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set { if (SetProperty(ref _errorMessage, value)) RaiseStatus(); }
    }

    public TimeSpan? Duration
    {
        get => _duration;
        set { if (SetProperty(ref _duration, value)) OnPropertyChanged(nameof(DurationText)); }
    }

    public string DurationText => _duration is { } d ? (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss")) : "";

    public bool IsPlaying
    {
        get => _isPlaying;
        set { if (SetProperty(ref _isPlaying, value)) RaiseStatus(); }
    }

    public bool IsPreviewing
    {
        get => _isPreviewing;
        set => SetProperty(ref _isPreviewing, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public bool IsMissing => _state == SoundState.Missing;
    public bool IsError => _state == SoundState.Error;

    public string StatusText => _state switch
    {
        SoundState.Missing => "Missing file",
        SoundState.Error => "Error",
        SoundState.Loading => "Checking...",
        _ => _isPlaying ? "Playing" : "Ready",
    };

    public string StatusTooltip => _state switch
    {
        SoundState.Missing => $"This sound has a hotkey but the file no longer exists:\n{FullPath}\nRestore the file or clear the hotkey.",
        SoundState.Error => _errorMessage ?? "This file could not be decoded.",
        _ => FullPath,
    };

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusTooltip));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(IsError));
    }
}

public sealed class CommandHotkeyViewModel : ObservableObject, IHotkeyTarget
{
    private Hotkey _hotkey;
    private string? _hotkeyError;

    public CommandHotkeyViewModel(string binding, string displayName, string description)
    {
        Binding = binding;
        DisplayName = displayName;
        Description = description;
    }

    public string Binding { get; }
    public string DisplayName { get; }
    public string Description { get; }

    public Hotkey Hotkey
    {
        get => _hotkey;
        set
        {
            if (SetProperty(ref _hotkey, value))
            {
                OnPropertyChanged(nameof(HotkeyText));
                OnPropertyChanged(nameof(HasHotkey));
            }
        }
    }

    public string HotkeyText => _hotkey.IsEmpty ? "Not set" : _hotkey.ToString();
    public bool HasHotkey => !_hotkey.IsEmpty;

    public string? HotkeyError
    {
        get => _hotkeyError;
        set { if (SetProperty(ref _hotkeyError, value)) OnPropertyChanged(nameof(HasHotkeyError)); }
    }

    public bool HasHotkeyError => !string.IsNullOrEmpty(_hotkeyError);
}

/// <summary>An entry in a device drop-down. Id null means "none" or "Windows default", depending on the list.</summary>
public sealed record DeviceOption(string? Id, string? Name, string Display, bool IsAvailable)
{
    public override string ToString() => Display;
}

public sealed record EnumOption<T>(T Value, string Display, string Description) where T : struct, Enum
{
    public override string ToString() => Display;
}
