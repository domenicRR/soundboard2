using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CS2Soundboard.Core.Audio;
using CS2Soundboard.Core.Hotkeys;
using CS2Soundboard.Core.Library;

namespace CS2Soundboard.ViewModels;

public sealed partial class MainViewModel
{
    private readonly Dictionary<string, SoundItemViewModel> _itemsByKey = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _rescanDebounce;
    private CancellationTokenSource? _preloadCts;
    private (int Rate, int Channels)? _preloadedFormat;
    private string _filterText = "";
    private string _libraryStatus = "No sound folder selected.";
    private StatusLevel _libraryStatusLevel = StatusLevel.Warning;
    private bool _isScanning;

    public ObservableCollection<SoundItemViewModel> Sounds { get; } = new();
    public ICollectionView SoundsView { get; private set; } = null!;

    public ICommand BrowseFolderCommand { get; private set; } = null!;
    public ICommand RescanCommand { get; private set; } = null!;
    public ICommand PlayCommand { get; private set; } = null!;
    public ICommand PreviewCommand { get; private set; } = null!;
    public ICommand AssignHotkeyCommand { get; private set; } = null!;
    public ICommand ClearHotkeyCommand { get; private set; } = null!;
    public ICommand OpenFolderCommand { get; private set; } = null!;

    private void InitLibraryCommands()
    {
        SoundsView = CollectionViewSource.GetDefaultView(Sounds);
        SoundsView.Filter = o => o is SoundItemViewModel s &&
                                 (string.IsNullOrWhiteSpace(_filterText)
                                  || s.Name.Contains(_filterText, StringComparison.CurrentCultureIgnoreCase)
                                  || s.Folder.Contains(_filterText, StringComparison.CurrentCultureIgnoreCase)
                                  || s.HotkeyText.Contains(_filterText, StringComparison.CurrentCultureIgnoreCase));

        BrowseFolderCommand = new AsyncCommand(_ => BrowseFolderAsync(), ReportException);
        RescanCommand = new AsyncCommand(_ => RescanAsync(), ReportException);
        PlayCommand = new AsyncCommand(p => p is SoundItemViewModel s ? PlayOrStopAsync(s) : Task.CompletedTask, ReportException);
        PreviewCommand = new AsyncCommand(p => p is SoundItemViewModel s ? PreviewAsync(s) : Task.CompletedTask, ReportException);
        AssignHotkeyCommand = new RelayCommand(p => { if (p is IHotkeyTarget t) AssignHotkey(t); });
        ClearHotkeyCommand = new RelayCommand(p => { if (p is IHotkeyTarget t) ClearHotkey(t); }, p => p is IHotkeyTarget t && !t.Hotkey.IsEmpty);
        OpenFolderCommand = new RelayCommand(OpenSoundFolder, () => Directory.Exists(_s.SoundDirectory));
        StopAllCommand = new RelayCommand(StopAll);
        StopCurrentCommand = new RelayCommand(() =>
        {
            var name = _playback.StopLast();
            if (name != null) AddEvent($"Stopped \"{name}\".");
        });
    }

    public string? SoundDirectory => _s.SoundDirectory;

    public bool IncludeSubfolders
    {
        get => _s.IncludeSubfolders;
        set
        {
            if (_s.IncludeSubfolders == value) return;
            _s.IncludeSubfolders = value;
            OnPropertyChanged();
            SaveSoon();
            _ = RescanAsync();
        }
    }

    public bool AutoRescan
    {
        get => _s.AutoRescan;
        set
        {
            if (_s.AutoRescan == value) return;
            _s.AutoRescan = value;
            OnPropertyChanged();
            SaveSoon();
            SetupWatcher();
        }
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value ?? "")) SoundsView.Refresh();
        }
    }

    public string LibraryStatus
    {
        get => _libraryStatus;
        private set => SetProperty(ref _libraryStatus, value);
    }

    public StatusLevel LibraryStatusLevel
    {
        get => _libraryStatusLevel;
        private set => SetProperty(ref _libraryStatusLevel, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    private async Task BrowseFolderAsync()
    {
        var dir = _ui.PickFolder(_s.SoundDirectory);
        if (string.IsNullOrEmpty(dir)) return;
        _s.SoundDirectory = dir;
        OnPropertyChanged(nameof(SoundDirectory));
        SaveSoon();
        await RescanAsync();
    }

    private void OpenSoundFolder()
    {
        try
        {
            if (Directory.Exists(_s.SoundDirectory))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_s.SoundDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { ReportException(ex); }
    }

    /// <summary>Scans the folder, keeps existing items/states, adds placeholders for missing hotkeyed files.</summary>
    public async Task RescanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            var dir = _s.SoundDirectory;
            bool sub = _s.IncludeSubfolders;
            var result = await Task.Run(() => SoundLibraryScanner.Scan(dir, sub));

            var newItems = new List<SoundItemViewModel>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in result.Files)
            {
                seen.Add(f.RelativePath);
                var folder = Path.GetDirectoryName(f.RelativePath) ?? "";
                if (_itemsByKey.TryGetValue(f.RelativePath, out var existing) && existing.State != SoundState.Missing
                    && string.Equals(existing.FullPath, f.FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    newItems.Add(existing);
                }
                else
                {
                    newItems.Add(new SoundItemViewModel(f.RelativePath, f.FullPath, f.Name, folder));
                }
            }

            // Sounds that still have a hotkey but whose file disappeared stay visible as "Missing".
            foreach (var kv in _s.Hotkeys)
            {
                if (seen.Contains(kv.Key)) continue;
                var full = string.IsNullOrEmpty(result.Directory) ? kv.Key : Path.Combine(result.Directory, kv.Key);
                newItems.Add(new SoundItemViewModel(kv.Key, full, Path.GetFileNameWithoutExtension(kv.Key), Path.GetDirectoryName(kv.Key) ?? "")
                {
                    State = SoundState.Missing,
                });
            }

            // Unregister hotkeys of items that are gone entirely.
            var newKeys = new HashSet<string>(newItems.Select(i => i.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var old in _itemsByKey.Values.Where(i => !newKeys.Contains(i.Key)).ToList())
                _hotkeys.Unregister(old.Binding);

            _itemsByKey.Clear();
            foreach (var i in newItems) _itemsByKey[i.Key] = i;
            Sounds.Clear();
            foreach (var i in newItems) Sounds.Add(i);

            foreach (var item in newItems)
            {
                item.Hotkey = _s.Hotkeys.TryGetValue(item.Key, out var text) && Hotkey.TryParse(text, out var hk) ? hk : Hotkey.None;
                item.HotkeyError = item.Hotkey.IsEmpty ? null : _hotkeys.Register(item.Binding, item.Hotkey);
            }

            int missing = newItems.Count(i => i.State == SoundState.Missing);
            int failedHotkeys = newItems.Count(i => i.HasHotkeyError);
            if (result.Error != null)
            {
                LibraryStatus = result.Error;
                LibraryStatusLevel = StatusLevel.Error;
                if (!string.IsNullOrEmpty(dir)) AddEvent(result.Error, StatusLevel.Error);
            }
            else
            {
                LibraryStatus = $"{result.Files.Count} sound(s) in {result.Directory}" +
                                (missing > 0 ? $"  -  {missing} hotkeyed file(s) missing" : "") +
                                (failedHotkeys > 0 ? $"  -  {failedHotkeys} hotkey(s) could not be registered" : "");
                LibraryStatusLevel = missing > 0 || failedHotkeys > 0 ? StatusLevel.Warning : StatusLevel.Ok;
                if (result.Files.Count == 0)
                {
                    LibraryStatus = $"No supported audio files found in {result.Directory}. Supported: WAV, MP3, OGG (and FLAC, M4A/AAC, WMA where Windows has the codec).";
                    LibraryStatusLevel = StatusLevel.Warning;
                }
            }

            _cache.Retain(result.Files.Select(f => f.FullPath));
            SetupWatcher();
            _preloadedFormat = null;
            StartValidationAndPreload();
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>Validates every file (duration, decodability) and pre-decodes clips for instant hotkey playback.</summary>
    private void StartValidationAndPreload()
    {
        _preloadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _preloadCts = cts;
        var items = Sounds.Where(i => i.State != SoundState.Missing).ToList();
        var graph = _audio.Graph;
        var format = graph != null ? (graph.WaveFormat.SampleRate, graph.WaveFormat.Channels) : ((int, int)?)null;
        _preloadedFormat = format;

        _ = Task.Run(async () =>
        {
            foreach (var item in items)
            {
                if (cts.IsCancellationRequested) return;
                if (item.State == SoundState.Loading || item.State == SoundState.Error)
                {
                    try
                    {
                        var d = AudioDecoder.GetDuration(item.FullPath);
                        await _dispatcher.InvokeAsync(() => { item.Duration = d; item.ErrorMessage = null; item.State = SoundState.Ready; });
                    }
                    catch (Exception ex)
                    {
                        var msg = ex is AudioDecodeException ? ex.Message : $"Cannot read '{item.Name}': {ex.Message}";
                        await _dispatcher.InvokeAsync(() => { item.ErrorMessage = msg; item.State = SoundState.Error; });
                        continue;
                    }
                }
            }

            if (format is not { } f) return;
            foreach (var item in items)
            {
                if (cts.IsCancellationRequested) return;
                if (item.State != SoundState.Ready) continue;
                try
                {
                    await _cache.GetOrLoadAsync(item.FullPath, f.Item1, f.Item2, cts.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    var msg = ex.Message;
                    await _dispatcher.InvokeAsync(() => { item.ErrorMessage = msg; item.State = SoundState.Error; });
                }
            }
        }, cts.Token);
    }

    /// <summary>Called from status refresh: re-preload when the virtual output format changes.</summary>
    private void PreloadIfFormatChanged()
    {
        var graph = _audio.Graph;
        if (graph == null) return;
        var f = (graph.WaveFormat.SampleRate, graph.WaveFormat.Channels);
        if (_preloadedFormat == f) return;
        var local = _audio.Local;
        if (local != null)
            _cache.RetainFormats(f, (local.WaveFormat.SampleRate, local.WaveFormat.Channels));
        else
            _cache.RetainFormats(f);
        StartValidationAndPreload();
    }

    private void SetupWatcher()
    {
        DisposeWatcher();
        if (!_s.AutoRescan || string.IsNullOrEmpty(_s.SoundDirectory) || !Directory.Exists(_s.SoundDirectory)) return;
        try
        {
            _rescanDebounce ??= new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, async (_, _) =>
            {
                _rescanDebounce!.Stop();
                await RescanAsync();
            }, _dispatcher) { IsEnabled = false };

            var w = new FileSystemWatcher(_s.SoundDirectory)
            {
                IncludeSubdirectories = _s.IncludeSubfolders,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler onChange = (_, _) => _dispatcher.BeginInvoke(new Action(() => { _rescanDebounce.Stop(); _rescanDebounce.Start(); }));
            w.Created += onChange;
            w.Deleted += onChange;
            w.Changed += onChange;
            w.Renamed += (s, e) => onChange(s, e);
            w.Error += (_, e) => _dispatcher.BeginInvoke(new Action(() =>
            {
                AddEvent($"Stopped watching the sound folder: {e.GetException().Message}. Use Rescan to refresh.", StatusLevel.Warning);
                DisposeWatcher();
            }));
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex)
        {
            AddEvent($"Automatic rescan unavailable for this folder: {ex.Message}", StatusLevel.Warning);
        }
    }

    private void DisposeWatcher()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    // ------------------------------------------------------------------
    // Playback
    // ------------------------------------------------------------------

    private async Task PlayOrStopAsync(SoundItemViewModel item)
    {
        if (_playback.IsPlaying(item.Key))
        {
            _playback.Stop(item.Key);
            return;
        }
        await PlayItemAsync(item, fromHotkey: false);
    }

    private async Task PlayItemAsync(SoundItemViewModel item, bool fromHotkey)
    {
        if (item.State == SoundState.Missing || !File.Exists(item.FullPath))
        {
            item.State = SoundState.Missing;
            AddEvent($"Cannot play \"{item.Name}\": the file no longer exists ({item.FullPath}).", StatusLevel.Error, notify: fromHotkey);
            return;
        }
        try
        {
            var error = await WithPushToTalkAsync(item,
                () => _playback.TriggerAsync(item.Key, item.FullPath, item.Name, _s.PlaybackMode, _s.RetriggerStops, _s.HearSoundsLocally));
            if (item.State == SoundState.Error) { item.State = SoundState.Ready; item.ErrorMessage = null; }
            Services.Log.Info($"Play \"{item.Name}\" ({(fromHotkey ? "hotkey" : "button")}): {(error ?? "sent to virtual microphone")}");
            if (error != null) AddEvent(error, StatusLevel.Error, notify: fromHotkey);
            else if (_audio.Graph != null && _params.SoundboardMuted)
                AddEvent($"\"{item.Name}\" is playing, but the soundboard is muted.", StatusLevel.Warning);
        }
        catch (AudioDecodeException ex)
        {
            item.ErrorMessage = ex.Message;
            item.State = SoundState.Error;
            AddEvent(ex.Message, StatusLevel.Error, notify: fromHotkey);
        }
    }

    private async Task PreviewAsync(SoundItemViewModel item)
    {
        if (item.State == SoundState.Missing || !File.Exists(item.FullPath))
        {
            item.State = SoundState.Missing;
            AddEvent($"Cannot preview \"{item.Name}\": the file no longer exists.", StatusLevel.Error);
            return;
        }
        try
        {
            await _playback.PreviewAsync(item.Key, item.FullPath, item.Name);
            if (_audio.Local == null)
                AddEvent("Preview unavailable: " + (_audio.Status.Local.Error ?? "no listening device is available."), StatusLevel.Error);
        }
        catch (AudioDecodeException ex)
        {
            item.ErrorMessage = ex.Message;
            item.State = SoundState.Error;
            AddEvent(ex.Message, StatusLevel.Error);
        }
    }
}
