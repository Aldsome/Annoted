using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Annoted.Core.Interfaces;
using Annoted.Core.Models;
using Annoted.Wpf.ViewModels;

namespace Annoted.Wpf.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private string _lastFindText = string.Empty;
    private System.Drawing.Font _editorFont = new("Cascadia Code", 11F);
    private string _lastDictationPreview = string.Empty;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _vm = vm;

        vm.FindRequested              += (_, _) => OnFind();
        vm.FindNextRequested          += (_, down) => OnFindNext(down);
        vm.ReplaceRequested           += (_, _) => OnReplace();
        vm.GoToLineRequested          += (_, _) => OnGoToLine();
        vm.ChooseFontRequested        += (_, _) => OnChooseFont();
        vm.ChooseEditorBackColorRequested += (_, _) => OnChooseEditorBackColor();
        vm.ChooseEditorForeColorRequested += (_, _) => OnChooseEditorForeColor();
        vm.ShowWordCountRequested     += (_, _) => OnShowWordCount();
        vm.ShowAboutRequested         += (_, _) => OnShowAbout();
        vm.ApplyThemeRequested        += (_, dark) => { ApplyTheme(dark); ReapplyColorOverrides(); vm.AudioSidebar.RefreshWaveform(); };
        vm.ChooseStorageLocationRequested += (_, _) => OnChooseStorageLocation();
        vm.ExportAsImageRequested     += (_, _) => OnExportAsImage();
        vm.PrintRequested             += (_, _) => OnPrint();
        vm.ShowAppearanceRequested    += (_, _) => OnShowAppearance();

        vm.AudioSidebar.ExportRequested  += (_, memo) => OnExportMemo(memo);
        vm.AudioSidebar.RenameRequested  += (_, memo) => OnRenameMemo(memo);
        vm.AudioSidebar.DeleteRequested  += (_, memo) => OnDeleteMemo(memo);
        vm.AudioSidebar.DictationSegmentReady += (_, e) => OnDictationSegment(e);
        vm.AudioSidebar.ModelDownloadRequested += async (_, _) => await OnDownloadModel();
        vm.ChangeWhisperModelRequested         += async (_, _) => await OnDownloadModel();

        BuildHotkeys(vm);
        ApplyTheme(vm.IsDarkMode);
        vm.Initialize();

        // Restore persisted custom editor colors over the active theme.
        var s = vm.Storage.LoadSettings();
        ApplyCustomColors(s.CustomEditorBackArgb, s.CustomEditorForeArgb, s.CustomAccentArgb);
    }

    private void BuildHotkeys(MainViewModel vm)
    {
        void Add(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods, System.Windows.Input.ICommand cmd)
            => InputBindings.Add(new System.Windows.Input.KeyBinding(cmd, key, mods));

        const System.Windows.Input.ModifierKeys ctrl = System.Windows.Input.ModifierKeys.Control;
        const System.Windows.Input.ModifierKeys none = System.Windows.Input.ModifierKeys.None;

        Add(System.Windows.Input.Key.N, ctrl, vm.NewTabCommand);
        Add(System.Windows.Input.Key.O, ctrl, vm.OpenDocumentCommand);
        Add(System.Windows.Input.Key.S, ctrl, vm.SaveDocumentCommand);
        Add(System.Windows.Input.Key.S, ctrl | System.Windows.Input.ModifierKeys.Shift, vm.SaveDocumentAsCommand);
        Add(System.Windows.Input.Key.W, ctrl, vm.CloseTabCommand);
        Add(System.Windows.Input.Key.Z, ctrl, vm.UndoCommand);
        Add(System.Windows.Input.Key.Y, ctrl, vm.RedoCommand);
        Add(System.Windows.Input.Key.F, ctrl, vm.FindCommand);
        Add(System.Windows.Input.Key.F3, none, vm.FindNextCommand);
        Add(System.Windows.Input.Key.F3, System.Windows.Input.ModifierKeys.Shift, vm.FindPreviousCommand);
        Add(System.Windows.Input.Key.H, ctrl, vm.ReplaceCommand);
        Add(System.Windows.Input.Key.G, ctrl, vm.GoToLineCommand);
        Add(System.Windows.Input.Key.P, ctrl, vm.PrintCommand);
        Add(System.Windows.Input.Key.OemPlus, ctrl, vm.ZoomInCommand);
        Add(System.Windows.Input.Key.Add, ctrl, vm.ZoomInCommand);       // numpad +
        Add(System.Windows.Input.Key.OemMinus, ctrl, vm.ZoomOutCommand);
        Add(System.Windows.Input.Key.Subtract, ctrl, vm.ZoomOutCommand); // numpad -
        Add(System.Windows.Input.Key.D0, ctrl, vm.ResetZoomCommand);
        Add(System.Windows.Input.Key.NumPad0, ctrl, vm.ResetZoomCommand);
        Add(System.Windows.Input.Key.F5, none, vm.InsertDateTimeCommand);

        // Ctrl + mouse wheel zooms the editor.
        PreviewMouseWheel += (_, e) =>
        {
            if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control) return;
            if (e.Delta > 0) vm.ZoomInCommand.Execute(null);
            else if (e.Delta < 0) vm.ZoomOutCommand.Execute(null);
            e.Handled = true;
        };
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => _vm.AudioSidebar.CancelModelDownload();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_vm is null) { base.OnClosing(e); return; }
        if (!_vm.ConfirmCloseAll()) { e.Cancel = true; return; }
        _vm.SaveAutosave();
        _vm.Dispose();
        base.OnClosing(e);
    }

    // ── Theme ─────────────────────────────────────────────────────────────────

    private void ApplyTheme(bool dark)
    {
        var theme = dark ? "Dark" : "Light";
        var themeUri = new Uri($"pack://application:,,,/Annoted;component/Themes/{theme}.xaml");
        var newTheme = new ResourceDictionary { Source = themeUri };

        var merged = Application.Current.Resources.MergedDictionaries;

        // Replace only the existing theme dictionary (the one that defines BackgroundBrush),
        // leaving converters/styles untouched. Swapping in place keeps DynamicResource
        // lookups resolving for already-realized visual trees (shell, sidebar, toolbar, editor).
        var existing = merged.FirstOrDefault(d =>
            d.Source is not null &&
            (d.Source.OriginalString.Contains("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
             d.Source.OriginalString.Contains("Dark.xaml", StringComparison.OrdinalIgnoreCase)));

        if (existing is not null)
        {
            var idx = merged.IndexOf(existing);
            merged[idx] = newTheme;
        }
        else
        {
            merged.Insert(0, newTheme);
        }
    }

    // ── Find/Replace ──────────────────────────────────────────────────────────

    private void OnFind()
    {
        var dlg = new InputDialog("Find", "Find what:", _lastFindText);
        if (dlg.ShowDialog(this) != true || string.IsNullOrEmpty(dlg.Result)) return;
        _lastFindText = dlg.Result;
        OnFindNext(true);
    }

    private void OnFindNext(bool searchDown)
    {
        if (string.IsNullOrEmpty(_lastFindText)) { OnFind(); return; }
        // RichTextBox find via TextRange
        var editor = GetActiveEditor();
        if (editor is null) return;
        var range = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd);
        var text = range.Text;
        var caret = editor.CaretPosition.GetTextRunLength(LogicalDirection.Backward);
        var comparison = StringComparison.CurrentCultureIgnoreCase;
        var idx = searchDown
            ? text.IndexOf(_lastFindText, Math.Min(text.Length, caret + 1), comparison)
            : text.LastIndexOf(_lastFindText, Math.Clamp(caret - 1, 0, text.Length - 1), comparison);

        if (idx < 0) { MessageBox.Show(this, $"Cannot find \"{_lastFindText}\".", "Find"); return; }

        var start = editor.Document.ContentStart.GetPositionAtOffset(idx + 1);
        var end   = start?.GetPositionAtOffset(_lastFindText.Length);
        if (start is not null && end is not null) editor.Selection.Select(start, end);
    }

    private void OnReplace()
    {
        // Strict parity with WinForms ReplaceText: two chained prompts, replace one match.
        var findDlg = new InputDialog("Replace", "Find what:", _lastFindText);
        if (findDlg.ShowDialog(this) != true || string.IsNullOrEmpty(findDlg.Result)) return;
        var findValue = findDlg.Result;

        var replaceDlg = new InputDialog("Replace", "Replace with:", string.Empty);
        if (replaceDlg.ShowDialog(this) != true) return; // Cancel → bail; empty string is allowed
        var replaceValue = replaceDlg.Result ?? string.Empty;

        _lastFindText = findValue;
        var editor = GetActiveEditor();
        if (editor is null) return;

        var comparison = StringComparison.CurrentCultureIgnoreCase;

        // If the current selection already equals the find text, replace it.
        if (editor.Selection.Text.Equals(findValue, comparison))
        {
            editor.Selection.Text = replaceValue;
            _vm.StatusText = "Replaced selection";
            return;
        }

        var range = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd);
        var text = range.Text;
        var caret = editor.CaretPosition.GetTextRunLength(LogicalDirection.Backward);
        var idx = text.IndexOf(findValue, Math.Clamp(caret, 0, Math.Max(0, text.Length)), comparison);
        if (idx < 0)
        {
            MessageBox.Show(this, $"Cannot find \"{findValue}\".", "Replace");
            return;
        }

        var start = editor.Document.ContentStart.GetPositionAtOffset(idx + 1);
        var end = start?.GetPositionAtOffset(findValue.Length);
        if (start is not null && end is not null)
        {
            var sel = new TextRange(start, end) { Text = replaceValue };
            editor.CaretPosition = sel.End;
            _vm.StatusText = "Replaced match";
        }
    }

    private void OnGoToLine()
    {
        var dlg = new InputDialog("Go To Line", "Line number:", string.Empty);
        if (dlg.ShowDialog(this) != true) return;
        if (!int.TryParse(dlg.Result, out var lineNum)) return;
        var editor = GetActiveEditor();
        if (editor is null) return;
        var lines = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.Split('\n');
        lineNum = Math.Clamp(lineNum, 1, lines.Length);
        var offset = lines.Take(lineNum - 1).Sum(l => l.Length + 1);
        var pos = editor.Document.ContentStart.GetPositionAtOffset(offset + 1);
        if (pos is not null) editor.CaretPosition = pos;
    }

    // ── Font ──────────────────────────────────────────────────────────────────

    private void OnChooseFont()
    {
        using var dlg = new System.Windows.Forms.FontDialog
        {
            Font = _editorFont,
            ShowEffects = false
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _editorFont = (System.Drawing.Font)dlg.Font.Clone();
        var family = new System.Windows.Media.FontFamily(_editorFont.Name);
        double size = _editorFont.SizeInPoints * 96.0 / 72.0; // pt → WPF DIP
        var style = _editorFont.Italic ? FontStyles.Italic : FontStyles.Normal;
        var weight = _editorFont.Bold ? FontWeights.Bold : FontWeights.Normal;

        foreach (var view in FindVisualChildren<EditorView>(this))
        {
            view.Editor.FontFamily = family;
            view.Editor.FontSize = size;
            view.Editor.FontStyle = style;
            view.Editor.FontWeight = weight;
        }
        _vm.StatusText = "Font updated";
    }

    // ── Appearance (theme + editor colors, one dialog) ──────────────────────────

    private System.Windows.Media.Color? _customBackColor;
    private System.Windows.Media.Color? _customForeColor;
    private System.Windows.Media.Color? _customAccentColor;
    private bool _backOverrideActive, _foreOverrideActive, _accentOverrideActive;

    /// <summary>Applies persisted custom colors (if any) on top of the active theme.</summary>
    public void ApplyCustomColors(int? backArgb, int? foreArgb, int? accentArgb)
    {
        _customBackColor = backArgb is { } b ? IntToColor(b) : null;
        _customForeColor = foreArgb is { } f ? IntToColor(f) : null;
        _customAccentColor = accentArgb is { } a ? IntToColor(a) : null;
        ReapplyColorOverrides();
    }

    private void ReapplyColorOverrides()
    {
        // Custom colors are root-dictionary overrides shadowing the merged theme keys.
        // When cleared (null), remove our override so the brush falls back to the theme.
        var r = Application.Current.Resources;
        SetOverride(r, "EditorBackBrush", _customBackColor, ref _backOverrideActive);
        SetOverride(r, "ForeBrush",       _customForeColor, ref _foreOverrideActive);
        SetOverride(r, "AccentBrush",     _customAccentColor, ref _accentOverrideActive);
    }

    private static void SetOverride(ResourceDictionary r, string key, System.Windows.Media.Color? c, ref bool active)
    {
        if (c is { } v) { r[key] = new System.Windows.Media.SolidColorBrush(v); active = true; }
        else if (active) { r.Remove(key); active = false; }
    }

    private void OnShowAppearance()
    {
        // Snapshot for Cancel/revert.
        var snapMode = _vm.ThemeMode;
        var snapBack = _customBackColor; var snapFore = _customForeColor; var snapAccent = _customAccentColor;

        var dlg = new AppearanceDialog(snapMode, snapBack, snapFore, snapAccent) { Owner = this };
        dlg.Preview = (mode, back, fore, accent) => PreviewAppearance(mode, back, fore, accent);

        if (dlg.ShowDialog() == true)
        {
            _vm.SetThemeMode(dlg.SelectedMode); // persists mode + applies theme
            _customBackColor = dlg.SelectedBack; _customForeColor = dlg.SelectedFore; _customAccentColor = dlg.SelectedAccent;
            ApplyTheme(_vm.IsDarkMode);
            ReapplyColorOverrides();

            var s = _vm.Storage.LoadSettings();
            s.CustomEditorBackArgb = _customBackColor is { } cb ? ColorToInt(cb) : null;
            s.CustomEditorForeArgb = _customForeColor is { } cf ? ColorToInt(cf) : null;
            s.CustomAccentArgb     = _customAccentColor is { } ca ? ColorToInt(ca) : null;
            _vm.Storage.SaveSettings(s);
            _vm.StatusText = "Appearance updated";
        }
        else
        {
            // Cancel: revert visuals to the snapshot (no persistence happened).
            PreviewAppearance(snapMode, snapBack, snapFore, snapAccent);
        }
    }

    private void PreviewAppearance(string mode, System.Windows.Media.Color? back,
        System.Windows.Media.Color? fore, System.Windows.Media.Color? accent)
    {
        ApplyTheme(_vm.ResolveDarkPublic(mode));
        _customBackColor = back; _customForeColor = fore; _customAccentColor = accent;
        ReapplyColorOverrides();
        _vm.AudioSidebar.RefreshWaveform(); // instant waveform theme/accent refresh
    }

    private static System.Windows.Media.Color IntToColor(int argb)
        => System.Windows.Media.Color.FromArgb(
            (byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));

    private static int ColorToInt(System.Windows.Media.Color c)
        => (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;

    // Legacy menu hooks (kept for binding compatibility) now route through Appearance.
    private void OnChooseEditorBackColor() => OnShowAppearance();
    private void OnChooseEditorForeColor() => OnShowAppearance();

    // ── Word count ────────────────────────────────────────────────────────────

    private void OnShowWordCount()
    {
        var text = _vm.ActiveTab?.Text ?? string.Empty;
        var words = string.IsNullOrWhiteSpace(text) ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var chars = text.Length;
        var lines = text.Split('\n').Length;
        MessageBox.Show(this, $"Lines: {lines}\nWords: {words}\nCharacters: {chars}", "Word Count");
    }

    // ── About ─────────────────────────────────────────────────────────────────

    private void OnShowAbout()
    {
        MessageBox.Show(this,
            "Annoted v2.0\n\nA Windows notes app with tabs, autosave, themes, and audio notes.\n\n© Aldsome 2026",
            "About Annoted");
    }

    // ── Storage ───────────────────────────────────────────────────────────────

    private void OnChooseStorageLocation()
    {
        if (_vm.Storage.IsPortable)
        {
            MessageBox.Show(this,
                $"Portable mode stores app data beside Annoted.exe in:\n{_vm.Storage.AppDataFolder}",
                "Storage Location", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose where Annoted stores autosave data and recordings.",
            SelectedPath = _vm.Storage.AppDataFolder,
            ShowNewFolderButton = true
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        try
        {
            _vm.MoveStorageTo(dlg.SelectedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(this, $"Could not change storage location.\n\n{ex.Message}", "Annoted",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Export tab as image ─────────────────────────────────────────────────────

    private void OnExportAsImage()
    {
        var tab = _vm.ActiveTab;
        if (tab is null) return;

        var baseName = tab.Model.CurrentFilePath is not null
            ? Path.GetFileNameWithoutExtension(tab.Model.CurrentFilePath) : "Untitled";

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            DefaultExt = "png",
            FileName = baseName + ".png",
            Filter = "PNG image (*.png)|*.png",
            OverwritePrompt = true,
            Title = "Export Tab As Image"
        };
        if (dlg.ShowDialog(this) != true) return;

        var editor = GetActiveEditor();
        if (editor is null) return;

        try
        {
            SaveEditorAsPng(editor, dlg.FileName);
            _vm.StatusText = $"Exported {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not export the image:\n\n{ex.Message}", "Annoted", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void SaveEditorAsPng(RichTextBox editor, string path)
    {
        const int width = 1200;
        const int padding = 32;
        var text = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text;
        if (string.IsNullOrEmpty(text)) text = " ";

        var typeface = new System.Windows.Media.Typeface(
            editor.FontFamily, editor.FontStyle, editor.FontWeight, System.Windows.FontStretches.Normal);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(editor);
        var formatted = new System.Windows.Media.FormattedText(
            text, System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight, typeface, editor.FontSize,
            editor.Foreground, dpi.PixelsPerDip)
        { MaxTextWidth = width - padding * 2 };

        var height = Math.Clamp((int)Math.Ceiling(formatted.Height) + padding * 2, 240, 20000);

        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(editor.Background ?? System.Windows.Media.Brushes.White, null,
                new Rect(0, 0, width, height));
            dc.DrawText(formatted, new System.Windows.Point(padding, padding));
        }

        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(visual);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    // ── Print ───────────────────────────────────────────────────────────────────

    private void OnPrint()
    {
        var editor = GetActiveEditor();
        if (editor is null) return;

        var pd = new System.Windows.Controls.PrintDialog();
        if (pd.ShowDialog() != true) return;

        try
        {
            var copy = new System.Windows.Documents.FlowDocument();
            var src = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text;
            copy.Blocks.Add(new System.Windows.Documents.Paragraph(new Run(src)));
            copy.FontFamily = editor.FontFamily;
            copy.FontSize = editor.FontSize;
            copy.PageWidth = pd.PrintableAreaWidth;
            copy.ColumnWidth = pd.PrintableAreaWidth;
            copy.PagePadding = new Thickness(48);

            IDocumentPaginatorSource paginator = copy;
            pd.PrintDocument(paginator.DocumentPaginator, "Annoted");
            _vm.StatusText = "Print sent";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not print the document.\n\n{ex.Message}", "Print Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Audio memo dialogs ────────────────────────────────────────────────────

    private void OnExportMemo(AudioMemoModel memo)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            DefaultExt = "wav",
            FileName = SanitizeFileName(memo.DisplayName) + ".wav",
            Filter = "WAV audio (*.wav)|*.wav|All files (*.*)|*.*",
            OverwritePrompt = true,
            Title = "Export Memoir"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.Copy(memo.FilePath, dlg.FileName, overwrite: true);
            _vm.StatusText = $"Exported {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Export failed: {ex.Message}", "Annoted", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRenameMemo(AudioMemoModel memo)
    {
        var dlg = new InputDialog("Rename Memoir", "Memoir name:", memo.DisplayName);
        if (dlg.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dlg.Result)) return;
        memo.DisplayName = dlg.Result.Trim();
        _vm.AudioSidebar.RefreshMemoList();
        _vm.SaveAutosave();
        _vm.StatusText = "Memoir renamed";
    }

    private void OnDeleteMemo(AudioMemoModel memo)
    {
        var result = MessageBox.Show(this,
            $"Delete \"{memo.DisplayName}\"? This removes the recording file.",
            "Delete Memoir", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            if (File.Exists(memo.FilePath)) File.Delete(memo.FilePath);
            foreach (var tab in _vm.Tabs)
            {
                tab.Model.AudioMemos.Remove(memo);
                if (tab.Model.ActiveAudioMemoId == memo.Id)
                {
                    var next = tab.Model.AudioMemos.Find(m => File.Exists(m.FilePath));
                    tab.Model.ActiveAudioMemoId = next?.Id;
                    tab.Model.AudioFilePath = next?.FilePath;
                    tab.Model.AudioDisplayName = next?.DisplayName;
                }
            }
            _vm.AudioSidebar.RefreshMemoList();
            _vm.SaveAutosave();
            _vm.StatusText = "Voice memo deleted";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Delete failed: {ex.Message}", "Annoted", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Dictation ─────────────────────────────────────────────────────────────

    private EditorView? GetActiveEditorView()
    {
        // Walk visual tree to find the current EditorView
        var tab = _vm.ActiveTab;
        if (tab is null) return null;
        return FindVisualChild<EditorView>(this);
    }

    private RichTextBox? GetActiveEditor()
    {
        return GetActiveEditorView()?.Editor;
    }

    private void OnDictationSegment(DictationSegmentEventArgs e)
    {
        var editorView = GetActiveEditorView();
        if (editorView is null) return;

        if (e.IsFinal)
        {
            // Commit the last live preview as normal text + trailing space (parity with
            // WinForms FinalizeDictationPreview), then clear the tracker.
            var commit = string.IsNullOrEmpty(_lastDictationPreview) ? string.Empty : _lastDictationPreview + " ";
            editorView.FinalizeGhostText(commit);
            _lastDictationPreview = string.Empty;
            if (_vm.ActiveTab is not null) _vm.ActiveTab.IsDirty = true;
        }
        else
        {
            _lastDictationPreview = e.Text;
            editorView.ShowGhostText(e.Text, _vm.IsDarkMode);
        }
    }

    private Task OnDownloadModel()
    {
        var dlg = new DownloadModelDialog(_vm.AudioSidebar) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.ChosenModelKey is not null)
        {
            // Fire-and-forget: download runs in the background, surfaced via the bottom hint bar.
            _ = _vm.AudioSidebar.StartModelDownloadAsync(dlg.ChosenModelKey, dlg.ChosenModelName);
        }
        return Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim('.', ' ').Length > 0 ? name : "recording";
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindVisualChild<T>(child);
            if (result is not null) return result;
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }
}
