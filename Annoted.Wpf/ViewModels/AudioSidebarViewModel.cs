using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using System.Windows;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Annoted.Core.Interfaces;
using Annoted.Core.Models;
using Annoted.Infrastructure.Services;

namespace Annoted.Wpf.ViewModels;

public sealed partial class AudioSidebarViewModel : ObservableObject, IDisposable
{
    private readonly AudioService _audio;
    private readonly DictationService _dictation;
    private readonly StorageService _storage;
    private readonly IWhisperModelManager _modelManager;
    private readonly System.Windows.Threading.DispatcherTimer _progressTimer;
    private readonly System.Windows.Threading.DispatcherTimer _animTimer;

    public ObservableCollection<MemoListItem> Memos { get; } = new();

    // Selectable recording input devices.
    public ObservableCollection<AudioInputDevice> InputDevices { get; } = new();

    [ObservableProperty] private AudioInputDevice? _selectedInputDevice;

    // Background model download (shown in the bottom hint bar, non-blocking).
    [ObservableProperty] private bool _downloadActive;
    [ObservableProperty] private string _downloadStatus = string.Empty;
    [ObservableProperty] private double _downloadPercent;
    private CancellationTokenSource? _downloadCts;

    /// <summary>Downloads a model in the background, reporting to the hint-bar properties.</summary>
    public async Task StartModelDownloadAsync(string modelKey, string modelName)
    {
        if (DownloadActive) return;
        _downloadCts = new CancellationTokenSource();
        DownloadActive = true;
        DownloadPercent = 0;
        DownloadStatus = $"Downloading {modelName}…";

        var progress = new Progress<(int Percent, string Label)>(p =>
        {
            DownloadPercent = p.Percent;
            DownloadStatus = $"{modelName}: {p.Label}";
        });

        try
        {
            await _modelManager.DownloadModelAsync(modelKey, progress, _downloadCts.Token);
            DownloadPercent = 100;
            DownloadStatus = $"{modelName} ready.";
            SetStatus?.Invoke($"{modelName} model ready.");
        }
        catch (OperationCanceledException)
        {
            DownloadStatus = "Download cancelled.";
        }
        catch (Exception ex)
        {
            DownloadStatus = $"Download failed: {ex.Message}";
        }
        finally
        {
            // Leave the final message visible briefly, then hide the bar.
            await Task.Delay(2500);
            DownloadActive = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    public void CancelModelDownload() => _downloadCts?.Cancel();

    [ObservableProperty] private MemoListItem? _selectedMemo;
    [ObservableProperty] private string _timeLabel = "00:00 / --:--";
    [ObservableProperty] private string _waveformLabel = "Waveform Preview";
    [ObservableProperty] private BitmapSource? _waveformImage;
    [ObservableProperty] private bool _showWaveform;
    [ObservableProperty] private bool _isDictating;
    [ObservableProperty] private float _dictationLevel;
    [ObservableProperty] private int _dictationAnimPhase;

    // Shared audio visualizer state (used for both record + dictate previews).
    [ObservableProperty] private bool _isVisualizerActive;   // recording OR dictating
    [ObservableProperty] private bool _isRecordingMode;      // recording-only → crescent; dictating → circle
    [ObservableProperty] private float _visualizerLevel;     // raw captured peak (0..1); the view smooths it

    // Button enabled states
    [ObservableProperty] private bool _canRecord = true;
    [ObservableProperty] private bool _canStop;
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private bool _canResume;
    [ObservableProperty] private bool _canPlay;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _canExport;
    [ObservableProperty] private bool _canRename;
    [ObservableProperty] private bool _canDelete;
    [ObservableProperty] private bool _canOpenLocation;
    [ObservableProperty] private bool _canDictate = true;

    // Fired so MainViewModel can resolve active document
    public Func<TabViewModel?>? GetActiveTab;
    public Func<IEnumerable<TabViewModel>>? GetAllTabs;
    public Action<TabViewModel>? SwitchToTab;
    public Action? RequestAutosave;
    public Action<string>? SetStatus;

    private DocumentModel? _activeRecordingDocument;
    private AudioMemoModel? _activeRecordingMemo;
    private bool _isDisposed;

    public AudioSidebarViewModel(
        AudioService audio,
        DictationService dictation,
        StorageService storage,
        IWhisperModelManager modelManager)
    {
        _audio = audio;
        _dictation = dictation;
        _storage = storage;
        _modelManager = modelManager;

        _audio.DeviceFellBack += (_, msg) => Application.Current.Dispatcher.Invoke(() => OnDeviceFellBack(msg));
        _dictation.DeviceFellBack += (_, msg) => Application.Current.Dispatcher.Invoke(() => OnDeviceFellBack(msg));
        _audio.PlaybackStopped += (_, _) => Application.Current.Dispatcher.Invoke(OnPlaybackStopped);
        _audio.WaveformReady += (_, e) => Application.Current.Dispatcher.Invoke(() => SetWaveformFromBytes(e.BitmapBytes));
        _audio.LevelChanged += (_, e) =>
        {
            // Feed the shared visualizer while recording (attack fast; timer decays).
            if (e.Level > VisualizerLevel) VisualizerLevel = e.Level;
        };

        _dictation.SegmentReady += (_, e) => Application.Current.Dispatcher.Invoke(() => OnDictationSegment(e));
        _dictation.LevelChanged += (_, e) =>
        {
            if (e.Level > DictationLevel) DictationLevel = e.Level;
            if (e.Level > VisualizerLevel) VisualizerLevel = e.Level;
        };

        _progressTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _progressTimer.Tick += (_, _) => UpdateProgress();

        _animTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(55) };
        _animTimer.Tick += (_, _) =>
        {
            DictationAnimPhase++;
            DictationLevel *= 0.80F;
            VisualizerLevel *= 0.82F; // smooth decay so the shape settles instead of jittering
            if (IsDictating)
                WaveformLabel = "Listening" + new string('.', (DictationAnimPhase / 6) % 4);
        };

        LoadInputDevices();
        LoadPreviewVolume();
    }

    public void LoadInputDevices()
    {
        InputDevices.Clear();
        foreach (var dev in AudioService.EnumerateInputDevices())
            InputDevices.Add(dev);

        var saved = _storage.LoadSettings().InputDeviceNumber;
        SelectedInputDevice = InputDevices.FirstOrDefault(d => d.Number == saved) ?? InputDevices.FirstOrDefault();
    }

    private static int CurrentAccentArgb()
    {
        if (Application.Current?.Resources["AccentBrush"] is System.Windows.Media.SolidColorBrush b)
            return (b.Color.R << 16) | (b.Color.G << 8) | b.Color.B;
        return (0 << 16) | (122 << 8) | 204; // default accent
    }

    private static bool CurrentIsDark()
        => Application.Current?.Resources.MergedDictionaries
            .Any(d => d.Source?.ToString().Contains("Dark", StringComparison.OrdinalIgnoreCase) == true) == true;

    /// <summary>Re-renders the current waveform with the active theme + accent (instant theme refresh).</summary>
    public void RefreshWaveform()
    {
        if (SelectedMemo is null) return;
        _ = _audio.UpdateWaveformAsync(SelectedMemo.Memo.FilePath, CurrentIsDark(), CurrentAccentArgb(), CancellationToken.None);
    }

    private void OnDeviceFellBack(string message)
    {
        // Reflect the forced fallback in the UI and persist it so it stays consistent.
        var settings = _storage.LoadSettings();
        settings.InputDeviceNumber = -1;
        _storage.SaveSettings(settings);
        SelectedInputDevice = InputDevices.FirstOrDefault(d => d.Number == -1);
        SetStatus?.Invoke(message);
    }

    partial void OnSelectedInputDeviceChanged(AudioInputDevice? value)
    {
        if (value is null) return;

        // Don't switch the device mid-capture; the change applies to the next session.
        _audio.InputDeviceNumber = value.Number;
        _dictation.InputDeviceNumber = value.Number;

        var settings = _storage.LoadSettings();
        settings.InputDeviceNumber = value.Number;
        _storage.SaveSettings(settings);
        SetStatus?.Invoke($"Input device: {value.Name}");
    }

    // ── Memo list ─────────────────────────────────────────────────────────────

    public ObservableCollection<string> SortModes { get; } = new() { "Date", "Name", "Duration" };
    [ObservableProperty] private string _sortMode = "Date";
    [ObservableProperty] private bool _sortAscending;
    partial void OnSortModeChanged(string value) => RefreshMemoList();
    partial void OnSortAscendingChanged(bool value) => RefreshMemoList();
    [RelayCommand] private void ToggleSortDirection() => SortAscending = !SortAscending;

    public void RefreshMemoList()
    {
        var selectedId = SelectedMemo?.Memo.Id; // preserve selection across re-sort
        Memos.Clear();
        if (GetAllTabs is null) return;

        var items = new List<MemoListItem>();
        foreach (var tab in GetAllTabs())
            foreach (var memo in tab.Model.AudioMemos)
            {
                var len = MemoLength(memo.FilePath);
                items.Add(new MemoListItem(memo, tab, FormatTime(len), len));
            }

        IOrderedEnumerable<MemoListItem> sorted = SortMode switch
        {
            "Name"     => items.OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            "Duration" => items.OrderBy(i => i.Length),
            _          => items.OrderBy(i => i.Created),
        };
        // Default (descending) shows newest/longest/Z-A first; ascending flips it.
        if (!SortAscending) sorted = ReverseOrder(sorted, SortMode);
        foreach (var i in sorted) Memos.Add(i);

        if (selectedId is not null)
            SelectedMemo = Memos.FirstOrDefault(m => m.Memo.Id == selectedId);
        UpdateButtonStates();
    }

    private static IOrderedEnumerable<MemoListItem> ReverseOrder(IEnumerable<MemoListItem> items, string mode) => mode switch
    {
        "Name"     => items.OrderByDescending(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        "Duration" => items.OrderByDescending(i => i.Length),
        _          => items.OrderByDescending(i => i.Created),
    };

    partial void OnSelectedMemoChanged(MemoListItem? value)
    {
        if (value is null) return;

        // Selection = preview target only. Do NOT open the linked note tab (double-click does that).
        // Reset playback/playhead so any selected Memoir (incl. newly created) previews from 0.
        _audio.StopPlayback();
        _pendingStartRatio = 0;
        PlaybackRatio = 0;

        var doc = value.OwnerTab.Model;
        doc.ActiveAudioMemoId = value.Memo.Id;
        doc.AudioFilePath = value.Memo.FilePath;
        doc.AudioDisplayName = value.Memo.DisplayName;

        _ = _audio.UpdateWaveformAsync(value.Memo.FilePath, CurrentIsDark(), CurrentAccentArgb(), CancellationToken.None);
        UpdateButtonStates();
    }

    /// <summary>Double-click on a Memoir: open its linked note tab (editor target).</summary>
    public void OpenSelectedMemoTab()
    {
        if (SelectedMemo is null) return;
        if (!ReferenceEquals(GetActiveTab?.Invoke(), SelectedMemo.OwnerTab))
            SwitchToTab?.Invoke(SelectedMemo.OwnerTab);
    }

    // ── Batch Memoir actions (single or multi-select) ───────────────────────────

    public void DeleteMemos(IEnumerable<MemoListItem> items)
    {
        foreach (var it in items.ToList())
        {
            try { if (File.Exists(it.Memo.FilePath)) File.Delete(it.Memo.FilePath); } catch { /* ignore */ }
            var doc = it.OwnerTab.Model;
            doc.AudioMemos.Remove(it.Memo);
            if (doc.ActiveAudioMemoId == it.Memo.Id)
            {
                var next = doc.AudioMemos.Find(m => File.Exists(m.FilePath));
                doc.ActiveAudioMemoId = next?.Id;
                doc.AudioFilePath = next?.FilePath;
                doc.AudioDisplayName = next?.DisplayName;
            }
        }
        _audio.StopPlayback();
        RefreshMemoList();
        RequestAutosave?.Invoke();
        SetStatus?.Invoke("Memoir(s) deleted");
    }

    public void ExportMemos(IEnumerable<MemoListItem> items, string folder)
    {
        var n = 0;
        foreach (var it in items)
        {
            try { File.Copy(it.Memo.FilePath, System.IO.Path.Combine(folder, Sanitize(it.DisplayName) + ".wav"), true); n++; }
            catch { /* ignore individual failures */ }
        }
        SetStatus?.Invoke($"Exported {n} Memoir(s)");
    }

    public void RenameMemos(IReadOnlyList<MemoListItem> items, string baseName)
    {
        baseName = baseName.Trim();
        if (items.Count == 0 || string.IsNullOrEmpty(baseName)) return;
        for (var i = 0; i < items.Count; i++)
            items[i].Memo.DisplayName = items.Count > 1 ? $"{baseName} {i + 1}" : baseName;
        RefreshMemoList();
        RequestAutosave?.Invoke();
        SetStatus?.Invoke("Memoir(s) renamed");
    }

    public void MoveMemosToActiveTab(IEnumerable<MemoListItem> items)
    {
        var target = GetActiveTab?.Invoke();
        if (target is not null) MoveMemos(items, target);
    }

    public void MoveMemos(IEnumerable<MemoListItem> items, TabViewModel targetTab)
    {
        foreach (var it in items.ToList())
        {
            if (ReferenceEquals(it.OwnerTab, targetTab)) continue;
            it.OwnerTab.Model.AudioMemos.Remove(it.Memo);
            targetTab.Model.AudioMemos.Add(it.Memo);
        }
        RefreshMemoList();
        RequestAutosave?.Invoke();
        SetStatus?.Invoke("Memoir(s) moved");
    }

    private static string Sanitize(string name)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim('.', ' ').Length > 0 ? name : "memoir";
    }

    // ── Recording ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Record()
    {
        var tab = GetActiveTab?.Invoke();
        if (tab is null || _audio.IsRecording || IsDictating) return;

        var doc = tab.Model;
        var folder = _storage.GetDocumentFolder(doc.Id);
        Directory.CreateDirectory(folder);

        var memoId = Guid.NewGuid().ToString("N");
        var displayName = $"{System.IO.Path.GetFileNameWithoutExtension(tab.Title.TrimEnd('•', ' '))} {DateTime.Now:g}";
        var memo = new AudioMemoModel
        {
            Id = memoId,
            FilePath = _storage.GetMemoFilePath(doc.Id, memoId),
            DisplayName = displayName,
            CreatedAtUtc = DateTime.UtcNow
        };

        _activeRecordingDocument = doc;
        _activeRecordingMemo = memo;

        _audio.StopPlayback();
        _audio.StartRecording(memo.FilePath, memo);
        _progressTimer.Start();

        // Shared visualizer: recording-only → crescent shape.
        VisualizerLevel = 0F;
        IsRecordingMode = true;
        IsVisualizerActive = true;
        ShowWaveform = true;
        if (!_animTimer.IsEnabled) _animTimer.Start();

        UpdateButtonStates();
        SetStatus?.Invoke("Recording high-quality WAV…");
    }

    [RelayCommand]
    private void StopAudio()
    {
        if (_audio.IsRecording || _audio.IsRecordingPaused)
        {
            _audio.StopRecording();
            if (_activeRecordingDocument is not null && _activeRecordingMemo is not null
                && File.Exists(_activeRecordingMemo.FilePath))
            {
                AddMemoToDocument(_activeRecordingDocument, _activeRecordingMemo);
                _activeRecordingDocument.ActiveAudioMemoId = _activeRecordingMemo.Id;
                _activeRecordingDocument.AudioFilePath = _activeRecordingMemo.FilePath;
                _activeRecordingDocument.AudioDisplayName = _activeRecordingMemo.DisplayName;
            }
            _activeRecordingDocument = null;
            _activeRecordingMemo = null;
            RequestAutosave?.Invoke();
            RefreshMemoList();
        }
        else
        {
            _audio.StopPlayback();
        }
        _progressTimer.Stop();
        TimeLabel = "00:00 / --:--";

        // Tear down the shared visualizer if dictation isn't also running.
        if (!IsDictating)
        {
            IsVisualizerActive = false;
            IsRecordingMode = false;
            VisualizerLevel = 0F;
            if (_animTimer.IsEnabled) _animTimer.Stop();
        }

        UpdateButtonStates();
        SetStatus?.Invoke("Stopped");
    }

    [RelayCommand]
    private void PauseAudio()
    {
        if (_audio.IsRecording) { _audio.PauseRecording(); SetStatus?.Invoke("Recording paused"); }
        else if (_audio.IsPlaying) { /* WaveOutEvent doesn't have Pause; stop and note position */ }
        UpdateButtonStates();
    }

    [RelayCommand]
    private void ResumeAudio()
    {
        if (_audio.IsRecordingPaused) { _audio.ResumeRecording(); SetStatus?.Invoke("Recording resumed"); }
        UpdateButtonStates();
    }

    [RelayCommand]
    private void PlaySelected()
    {
        var item = SelectedMemo;
        if (item is null || _audio.IsRecording || _audio.IsRecordingPaused) return;
        if (_audio.IsPlaying) { _audio.StopPlayback(); }
        else
        {
            if (_pendingStartRatio > 0) _audio.StartPlaybackAt(item.Memo.FilePath, _pendingStartRatio);
            else _audio.StartPlayback(item.Memo.FilePath);
            _progressTimer.Start();
            SetStatus?.Invoke("Playing recording");
        }
        UpdateButtonStates();
    }

    [RelayCommand]
    private void Export()
    {
        var item = SelectedMemo;
        if (item is null) return;
        // Dialog shown in code-behind via event/callback to keep VM clean
        ExportRequested?.Invoke(this, item.Memo);
    }

    public event EventHandler<AudioMemoModel>? ExportRequested;

    [RelayCommand]
    private void Rename()
    {
        var item = SelectedMemo;
        if (item is null) return;
        RenameRequested?.Invoke(this, item.Memo);
    }

    public event EventHandler<AudioMemoModel>? RenameRequested;

    [RelayCommand]
    private void Delete()
    {
        var item = SelectedMemo;
        if (item is null) return;
        DeleteRequested?.Invoke(this, item.Memo);
    }

    public event EventHandler<AudioMemoModel>? DeleteRequested;

    [RelayCommand]
    private void OpenLocation()
    {
        var item = SelectedMemo;
        if (item?.Memo.FilePath is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{item.Memo.FilePath}\"",
                UseShellExecute = true
            });
        }
        catch { /* ignore */ }
    }

    // ── Dictation ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleDictation()
    {
        if (IsDictating) { await StopDictationAsync(); }
        else { await StartDictationAsync(); }
    }

    private async Task StartDictationAsync()
    {
        if (_audio.IsRecording || _audio.IsRecordingPaused) return;
        try
        {
            SetStatus?.Invoke("Checking speech model…");
            var modelPath = _modelManager.FindExistingModel();
            if (modelPath is null)
            {
                ModelDownloadRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            SetStatus?.Invoke("Loading speech model…");
            await _dictation.StartAsync(modelPath);
            IsDictating = true;

            DictationAnimPhase = 0;
            DictationLevel = 0F;

            // Shared visualizer: dictation → full circle.
            VisualizerLevel = 0F;
            IsRecordingMode = false;
            IsVisualizerActive = true;
            if (!_animTimer.IsEnabled) _animTimer.Start();

            ShowWaveform = true;
            WaveformLabel = "Listening.";
            UpdateButtonStates();
            SetStatus?.Invoke("Dictating… speak now (gray text is the live preview)");
        }
        catch (Exception ex)
        {
            SetStatus?.Invoke($"Dictation error: {ex.Message}");
            await StopDictationAsync();
        }
    }

    public event EventHandler? ModelDownloadRequested;

    private async Task StopDictationAsync()
    {
        await _dictation.StopAsync();
        IsDictating = false;
        DictationLevel = 0F;
        WaveformLabel = "Waveform Preview";

        // Tear down the shared visualizer if recording isn't also running.
        if (!_audio.IsRecording && !_audio.IsRecordingPaused)
        {
            _animTimer.Stop();
            IsVisualizerActive = false;
            IsRecordingMode = false;
            VisualizerLevel = 0F;
        }

        UpdateButtonStates();
        SetStatus?.Invoke("Dictation stopped");
    }

    private void OnDictationSegment(DictationSegmentEventArgs e)
    {
        DictationSegmentReady?.Invoke(this, e);
    }

    public event EventHandler<DictationSegmentEventArgs>? DictationSegmentReady;

    // ── Progress / waveform ───────────────────────────────────────────────────

    private void UpdateProgress()
    {
        if (_audio.IsRecording)
        {
            // Recording — just show elapsed (no total)
            return;
        }
        var prog = _audio.GetPlaybackProgress();
        if (prog is null) { _progressTimer.Stop(); return; }
        TimeLabel = $"{FormatTime(prog.Position)} / {FormatTime(prog.Duration)}";
        PlaybackRatio = prog.Ratio;
    }

    // Playback position (0..1) for the waveform playhead.
    [ObservableProperty] private double _playbackRatio;
    private double _pendingStartRatio;

    // Preview volume (0..1) — playback only, never the recorded/exported file. Persisted.
    [ObservableProperty] private double _previewVolume = 1.0;
    partial void OnPreviewVolumeChanged(double value)
    {
        _audio.PlaybackVolume = (float)value;
        if (_volumeLoaded)
        {
            var s = _storage.LoadSettings();
            s.PreviewVolume = value;
            _storage.SaveSettings(s);
        }
    }
    private bool _volumeLoaded;

    public void LoadPreviewVolume()
    {
        PreviewVolume = Math.Clamp(_storage.LoadSettings().PreviewVolume, 0, 1);
        _audio.PlaybackVolume = (float)PreviewVolume;
        _volumeLoaded = true;
    }

    /// <summary>Single click: if playing → pause + move playhead to clicked position; else just set position.</summary>
    public void SetPlayheadPosition(double ratio)
    {
        if (SelectedMemo is null || _audio.IsRecording || _audio.IsRecordingPaused) return;
        ratio = Math.Clamp(ratio, 0, 1);
        _pendingStartRatio = ratio;          // resume point for next play
        if (_audio.IsPlaying) _audio.StopPlayback(); // pause; OnPlaybackStopped keeps the position
        PlaybackRatio = ratio;
    }

    /// <summary>Double click: play from the exact clicked position (seek-before-play, no start jump).</summary>
    public async Task PlayFromAsync(double ratio)
    {
        var item = SelectedMemo;
        if (item is null || _audio.IsRecording || _audio.IsRecordingPaused) return;
        ratio = Math.Clamp(ratio, 0, 1);

        _pendingStartRatio = ratio;
        PlaybackRatio = ratio;
        await Task.Delay(20);                                  // declick
        _audio.StartPlaybackAt(item.Memo.FilePath, ratio);     // positioned before Play
        _pendingStartRatio = 0;
        _progressTimer.Start();
        IsPlaying = _audio.IsPlaying;
        UpdateButtonStates();
    }

    /// <summary>Live playback ratio for smooth playhead animation.</summary>
    public double GetLivePlaybackRatio()
        => _audio.GetPlaybackProgress()?.Ratio ?? PlaybackRatio;

    private void OnPlaybackStopped()
    {
        _progressTimer.Stop();
        IsPlaying = false;
        TimeLabel = "00:00 / --:--";
        PlaybackRatio = _pendingStartRatio; // keep paused/seek position (0 after natural stop)
        UpdateButtonStates();
    }

    private void SetWaveformFromBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        WaveformImage = decoder.Frames[0];
        ShowWaveform = true;
    }

    // ── Button states ─────────────────────────────────────────────────────────

    private void UpdateButtonStates()
    {
        var isRec = _audio.IsRecording;
        var isPaused = _audio.IsRecordingPaused;
        var hasSelection = SelectedMemo is not null && File.Exists(SelectedMemo.Memo.FilePath);
        var isPlay = _audio.IsPlaying;

        CanRecord = !isRec && !isPaused && !IsDictating;
        CanStop   = isRec || isPaused || isPlay;
        CanPause  = isRec || isPlay;
        CanResume = isPaused;
        CanPlay   = !isRec && !isPaused && hasSelection;
        IsPlaying = isPlay;
        CanExport = !isRec && !isPaused && hasSelection;
        CanRename = !isRec && !isPaused && hasSelection;
        CanDelete = !isRec && !isPaused && hasSelection;
        CanOpenLocation = hasSelection;
        CanDictate = IsDictating || (!isRec && !isPaused);
        ShowWaveform = hasSelection || isRec || isPaused || IsDictating;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AddMemoToDocument(DocumentModel doc, AudioMemoModel memo)
    {
        var idx = doc.AudioMemos.FindIndex(m => string.Equals(m.Id, memo.Id, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) doc.AudioMemos[idx] = memo;
        else doc.AudioMemos.Add(memo);
    }

    private static TimeSpan MemoLength(string filePath)
    {
        try { using var r = new NAudio.Wave.AudioFileReader(filePath); return r.TotalTime; }
        catch { return TimeSpan.Zero; }
    }

    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _progressTimer.Stop();
        _animTimer.Stop();
        _audio.Dispose();
        _dictation.Dispose();
    }
}

public sealed class MemoListItem(AudioMemoModel memo, TabViewModel ownerTab, string duration, TimeSpan length)
{
    public AudioMemoModel Memo { get; } = memo;
    public TabViewModel OwnerTab { get; } = ownerTab;
    public string Duration { get; } = duration;
    public TimeSpan Length { get; } = length;
    public DateTime Created => Memo.CreatedAtUtc;
    public string DisplayName => Memo.DisplayName;
    public string TabTitle => OwnerTab.Title;
}
