using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Annoted.Core.Models;
using Annoted.Infrastructure.Services;

namespace Annoted.Wpf.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly StorageService _storage;
    public StorageService Storage => _storage;
    public AudioSidebarViewModel AudioSidebar { get; }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    [ObservableProperty] private TabViewModel? _activeTab;
    [ObservableProperty] private bool _isDarkMode;
    [ObservableProperty] private bool _isWordWrap;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _windowTitle = "Annoted";
    [ObservableProperty] private float _zoomFactor = 1F;

    private string _lastFindText = string.Empty;
    private bool _isRestoringSession;
    private bool _isDisposed;

    public event EventHandler? FindRequested;
    public event EventHandler<bool>? FindNextRequested;
    public event EventHandler? ReplaceRequested;
    public event EventHandler? GoToLineRequested;
    public event EventHandler? ChooseFontRequested;
    public event EventHandler? ChooseEditorBackColorRequested;
    public event EventHandler? ChooseEditorForeColorRequested;
    public event EventHandler? ExportAsImageRequested;
    public event EventHandler? PrintRequested;
    public event EventHandler? ChooseStorageLocationRequested;
    public event EventHandler? ShowWordCountRequested;
    public event EventHandler? ShowAboutRequested;

    public MainViewModel(StorageService storage, AudioSidebarViewModel audioSidebar)
    {
        _storage = storage;
        AudioSidebar = audioSidebar;

        AudioSidebar.GetActiveTab = () => ActiveTab;
        AudioSidebar.GetAllTabs = () => Tabs;
        AudioSidebar.SwitchToTab = tab => ActiveTab = tab;
        AudioSidebar.RequestAutosave = SaveAutosave;
        AudioSidebar.SetStatus = msg => StatusText = msg;

        // Resolve the persisted theme mode (system/light/dark) into the initial dark flag
        // without firing OnIsDarkModeChanged (no subscriber yet; MainWindow applies on load).
        _themeMode = _storage.LoadSettings().ThemeMode ?? "system";
        _isDarkMode = ResolveDark(_themeMode);

        // Follow the OS theme live while in "system" mode.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
    }

    // ── Theme mode (system / light / dark) ──────────────────────────────────────

    public string ThemeMode
    {
        get => _themeMode;
        private set => _themeMode = value;
    }
    private string _themeMode = "system";

    public bool ResolveDarkPublic(string mode) => ResolveDark(mode);

    private static bool ResolveDark(string mode) => mode switch
    {
        "dark"  => true,
        "light" => false,
        _       => SystemTheme.IsDark(),
    };

    /// <summary>Sets the theme mode, persists it, and re-resolves the active theme.</summary>
    public void SetThemeMode(string mode)
    {
        _themeMode = mode;
        var settings = _storage.LoadSettings();
        settings.ThemeMode = mode;
        _storage.SaveSettings(settings);
        IsDarkMode = ResolveDark(mode); // fires OnIsDarkModeChanged → ApplyThemeRequested
    }

    private void OnSystemPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
        if (!string.Equals(_themeMode, "system", StringComparison.OrdinalIgnoreCase)) return;
        Application.Current?.Dispatcher.Invoke(() => IsDarkMode = SystemTheme.IsDark());
    }

    public void Initialize()
    {
        if (!LoadAutosave())
            CreateTab();

        UpdateWindowTitle();
    }

    // ── Tabs ──────────────────────────────────────────────────────────────────

    [RelayCommand]
    public void NewTab() => CreateTab();

    private TabViewModel CreateTab(
        string text = "",
        string? filePath = null,
        string? documentId = null,
        bool isDirty = false,
        IReadOnlyList<string>? undoHistory = null,
        IReadOnlyList<string>? redoHistory = null,
        float zoomFactor = 1F,
        bool selectTab = true)
    {
        var model = new DocumentModel
        {
            Id = string.IsNullOrWhiteSpace(documentId) ? Guid.NewGuid().ToString("N") : documentId,
            CurrentFilePath = filePath,
            IsDirty = isDirty,
            ZoomFactor = zoomFactor
        };

        if (undoHistory is not null) model.UndoHistory.AddRange(undoHistory.TakeLast(50));
        if (redoHistory is not null) model.RedoHistory.AddRange(redoHistory.TakeLast(10));

        var vm = new TabViewModel(model) { IsWordWrap = IsWordWrap };
        vm.SetText(text);

        Tabs.Add(vm);
        if (selectTab) ActiveTab = vm;

        if (!_isRestoringSession) { UpdateWindowTitle(); SaveAutosave(); }
        return vm;
    }

    [RelayCommand]
    public void CloseTab()
    {
        if (ActiveTab is null) return;
        if (!ConfirmClose(ActiveTab)) return;

        var idx = Tabs.IndexOf(ActiveTab);
        Tabs.Remove(ActiveTab);

        if (Tabs.Count == 0) CreateTab();
        else ActiveTab = Tabs[Math.Min(idx, Tabs.Count - 1)];

        UpdateWindowTitle();
        SaveAutosave();
    }

    private bool ConfirmClose(TabViewModel tab)
    {
        if (!tab.IsDirty) return true;
        var name = tab.Model.CurrentFilePath is not null
            ? Path.GetFileName(tab.Model.CurrentFilePath) : "Untitled";
        var result = MessageBox.Show(
            $"Save changes to \"{name}\"?",
            "Annoted",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes) SaveDocument(tab);
        return true;
    }

    partial void OnActiveTabChanged(TabViewModel? value)
    {
        if (value is null) return;
        UpdateWindowTitle();
        AudioSidebar.RefreshMemoList();
        StatusText = $"Active tab: {value.Title}";
    }

    // ── File operations ───────────────────────────────────────────────────────

    [RelayCommand]
    public void OpenDocument()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "Open Text File"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            CreateTab(File.ReadAllText(dlg.FileName), dlg.FileName, isDirty: false);
            StatusText = $"Opened {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not open file: {ex.Message}", "Annoted", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    public void SaveDocument() => SaveDocument(ActiveTab);

    [RelayCommand]
    public void SaveDocumentAs() => SaveDocumentAs(ActiveTab);

    public bool SaveDocument(TabViewModel? tab)
    {
        if (tab is null) return false;
        if (tab.Model.CurrentFilePath is null) return SaveDocumentAs(tab);
        return SaveToPath(tab, tab.Model.CurrentFilePath);
    }

    public bool SaveDocumentAs(TabViewModel? tab)
    {
        if (tab is null) return false;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            DefaultExt = "txt",
            FileName = tab.Model.CurrentFilePath is null ? "Untitled.txt" : Path.GetFileName(tab.Model.CurrentFilePath),
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            OverwritePrompt = true,
            Title = "Save Text File"
        };
        if (dlg.ShowDialog() != true) return false;
        return SaveToPath(tab, dlg.FileName);
    }

    private bool SaveToPath(TabViewModel tab, string filePath)
    {
        try
        {
            var wasUntitled = tab.Model.CurrentFilePath is null;
            File.WriteAllText(filePath, tab.Text);
            tab.Model.CurrentFilePath = filePath;
            tab.Model.IsDirty = false;
            tab.IsDirty = false;

            // Memos recorded while the tab was Untitled keep an "Untitled …" display name;
            // re-point them to the saved note name so the labels stay in sync.
            if (wasUntitled)
            {
                var newBase = Path.GetFileNameWithoutExtension(filePath);
                var changed = false;
                foreach (var memo in tab.Model.AudioMemos)
                {
                    if (memo.DisplayName.StartsWith("Untitled", StringComparison.OrdinalIgnoreCase))
                    {
                        memo.DisplayName = newBase + memo.DisplayName["Untitled".Length..];
                        changed = true;
                    }
                }
                if (changed) { AudioSidebar.RefreshMemoList(); SaveAutosave(); }
            }

            UpdateWindowTitle();
            StatusText = $"Saved {Path.GetFileName(filePath)}";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not save file: {ex.Message}", "Annoted", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ── Edit operations ───────────────────────────────────────────────────────

    [RelayCommand] public void Undo() => ActiveTab?.UndoCommand.Execute(null);
    [RelayCommand] public void Redo() => ActiveTab?.RedoCommand.Execute(null);
    [RelayCommand] public void Find() => FindRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] public void FindNext() => FindNextRequested?.Invoke(this, true);
    [RelayCommand] public void FindPrevious() => FindNextRequested?.Invoke(this, false);
    [RelayCommand] public void Replace() => ReplaceRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] public void GoToLine() => GoToLineRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] public void InsertDateTime()
        => ActiveTab?.SetText(ActiveTab.Text + DateTime.Now.ToString("g"));

    // ── Format ────────────────────────────────────────────────────────────────

    [RelayCommand]
    public void ToggleWordWrap()
    {
        IsWordWrap = !IsWordWrap;
        foreach (var tab in Tabs) tab.IsWordWrap = IsWordWrap;
    }

    [RelayCommand] public void ChooseFont() => ChooseFontRequested?.Invoke(this, EventArgs.Empty);

    // ── View ──────────────────────────────────────────────────────────────────

    [RelayCommand] public void ZoomIn() => SetZoom(ZoomFactor + 0.1F);
    [RelayCommand] public void ZoomOut() => SetZoom(ZoomFactor - 0.1F);
    [RelayCommand] public void ResetZoom() => SetZoom(1F);

    private void SetZoom(float value)
    {
        ZoomFactor = Math.Clamp(value, 0.5F, 5F);
        if (ActiveTab is not null) ActiveTab.ZoomFactor = ZoomFactor;
    }

    [RelayCommand] public void ShowWordCount() => ShowWordCountRequested?.Invoke(this, EventArgs.Empty);

    // ── Theme ─────────────────────────────────────────────────────────────────

    [RelayCommand]
    public void ToggleDarkMode() => SetThemeMode(IsDarkMode ? "light" : "dark");

    // Single source of truth: any change to IsDarkMode (menu checkbox, command, or restore)
    // applies the theme and persists. Avoids the double-toggle that made the menu a no-op.
    partial void OnIsDarkModeChanged(bool value)
    {
        ApplyThemeRequested?.Invoke(this, value);
        if (!_isRestoringSession) SaveAutosave();
    }

    public event EventHandler<bool>? ApplyThemeRequested;

    [RelayCommand] public void ChooseEditorBackColor() => ChooseEditorBackColorRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] public void ChooseEditorForeColor() => ChooseEditorForeColorRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] public void ResetCustomColors() { /* colors managed by MainWindow */ }

    [RelayCommand] public void ShowAppearance() => ShowAppearanceRequested?.Invoke(this, EventArgs.Empty);
    public event EventHandler? ShowAppearanceRequested;

    // ── Audio menu ────────────────────────────────────────────────────────────

    [RelayCommand] public void RecordAudio() => AudioSidebar.RecordCommand.Execute(null);
    [RelayCommand] public void StopAudio() => AudioSidebar.StopAudioCommand.Execute(null);
    [RelayCommand] public void PlayAudio() => AudioSidebar.PlaySelectedCommand.Execute(null);
    [RelayCommand] public async Task ToggleDictationMenu() => await AudioSidebar.ToggleDictationCommand.ExecuteAsync(null);
    [RelayCommand] public void ChangeWhisperModel() => ChangeWhisperModelRequested?.Invoke(this, EventArgs.Empty);
    public event EventHandler? ChangeWhisperModelRequested;

    // ── Help ──────────────────────────────────────────────────────────────────

    [RelayCommand] public void ShowAbout() => ShowAboutRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] public void ExportAsImage() => ExportAsImageRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] public void Print() => PrintRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] public void ChooseStorageLocation() => ChooseStorageLocationRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Moves storage and rebases all in-memory audio paths (parity with WinForms RefreshDocumentAudioPaths).</summary>
    public void MoveStorageTo(string newFolder)
    {
        var previous = _storage.MoveStorage(newFolder);

        foreach (var tab in Tabs)
        {
            var doc = tab.Model;
            foreach (var memo in doc.AudioMemos)
            {
                var rebased = _storage.RebaseStoragePath(memo.FilePath, previous);
                if (rebased is not null) memo.FilePath = rebased;
            }

            if (doc.AudioFilePath is not null)
            {
                var rebased = _storage.RebaseStoragePath(doc.AudioFilePath, previous);
                if (rebased is not null) doc.AudioFilePath = rebased;
            }
        }

        SaveAutosave();
        AudioSidebar.RefreshMemoList();
        StatusText = $"Storage: {_storage.AppDataFolder}";
    }

    // ── Autosave ──────────────────────────────────────────────────────────────

    private bool LoadAutosave()
    {
        var state = _storage.LoadAutosave();
        if (state is null) return false;

        _isRestoringSession = true;
        try
        {
            // Theme is driven by ThemeMode (AppSettings), not the autosave flag.
            IsWordWrap = state.IsWordWrap;

            var activeIdx = Math.Clamp(state.ActiveTabIndex, 0, state.Documents.Count - 1);
            for (var i = 0; i < state.Documents.Count; i++)
            {
                var doc = state.Documents[i];
                var tab = CreateTab(
                    doc.Text,
                    doc.CurrentFilePath,
                    doc.DocumentId,
                    doc.IsDirty,
                    doc.UndoHistory,
                    doc.RedoHistory,
                    doc.ZoomFactor,
                    selectTab: false);

                tab.Model.AudioFilePath = doc.AudioFilePath is not null
                    ? _storage.ResolveDocumentAudioPath(doc.AudioFilePath) : null;
                tab.Model.AudioDisplayName = doc.AudioDisplayName;
                tab.Model.ActiveAudioMemoId = doc.ActiveAudioMemoId;

                // Restore memo list (includes legacy migration)
                RestoreMemos(tab.Model, doc);
            }

            if (Tabs.Count > 0)
                ActiveTab = Tabs[activeIdx];

            StatusText = "Autosave restored";
            return true;
        }
        catch
        {
            StatusText = "Autosave restore failed";
            return false;
        }
        finally
        {
            _isRestoringSession = false;
        }
    }

    private void RestoreMemos(DocumentModel doc, DocumentAutosaveState state)
    {
        foreach (var ms in state.AudioMemos)
        {
            if (ms.Id is null || ms.FilePath is null) continue;
            var resolved = _storage.ResolveDocumentAudioPath(ms.FilePath);
            doc.AudioMemos.Add(new AudioMemoModel
            {
                Id = ms.Id,
                FilePath = resolved,
                DisplayName = ms.DisplayName ?? string.Empty,
                CreatedAtUtc = ms.CreatedAtUtc
            });
        }

        // Legacy migration: if no memos list but there is an audio path, synthesize one
        if (doc.AudioMemos.Count == 0 && doc.AudioFilePath is not null && File.Exists(doc.AudioFilePath))
        {
            var memo = new AudioMemoModel
            {
                Id = Guid.NewGuid().ToString("N"),
                FilePath = doc.AudioFilePath,
                DisplayName = string.IsNullOrWhiteSpace(doc.AudioDisplayName)
                    ? "Memoir" : doc.AudioDisplayName,
                CreatedAtUtc = DateTime.UtcNow
            };
            doc.AudioMemos.Add(memo);
            doc.ActiveAudioMemoId = memo.Id;
        }
    }

    public void SaveAutosave()
    {
        var state = new AutosaveState
        {
            ActiveTabIndex = ActiveTab is not null ? Tabs.IndexOf(ActiveTab) : 0,
            IsDarkMode = IsDarkMode,
            IsWordWrap = IsWordWrap,
        };

        foreach (var tab in Tabs)
        {
            var doc = tab.Model;
            state.Documents.Add(new DocumentAutosaveState
            {
                DocumentId = doc.Id,
                AudioFilePath = _storage.GetPersistedAudioPath(doc.AudioFilePath),
                AudioDisplayName = doc.AudioDisplayName,
                ActiveAudioMemoId = doc.ActiveAudioMemoId,
                AudioMemos = doc.AudioMemos.Select(m => new AudioMemoState
                {
                    Id = m.Id,
                    FilePath = _storage.GetPersistedAudioPath(m.FilePath),
                    DisplayName = m.DisplayName,
                    CreatedAtUtc = m.CreatedAtUtc
                }).ToList(),
                Text = tab.Text,
                CurrentFilePath = doc.CurrentFilePath,
                IsDirty = tab.IsDirty,
                ZoomFactor = tab.ZoomFactor,
                UndoHistory = doc.UndoHistory.TakeLast(50).ToList(),
                RedoHistory = doc.RedoHistory.TakeLast(10).ToList()
            });
        }

        _storage.SaveAutosave(state);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void UpdateWindowTitle()
    {
        var name = ActiveTab?.Model.CurrentFilePath is not null
            ? Path.GetFileName(ActiveTab.Model.CurrentFilePath)
            : "Untitled";
        WindowTitle = $"{name} — Annoted";
    }

    public bool ConfirmCloseAll()
    {
        foreach (var tab in Tabs.ToList())
            if (!ConfirmClose(tab)) return false;
        return true;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        AudioSidebar.Dispose();
    }
}
