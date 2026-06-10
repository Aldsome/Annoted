using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Annoted.Wpf.ViewModels;

namespace Annoted.Wpf.Views;

public partial class EditorView : UserControl
{
    private bool _suppressTextChange;

    public EditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is TabViewModel oldVm)
            oldVm.PropertyChanged -= Vm_PropertyChanged;

        if (e.NewValue is TabViewModel vm)
        {
            vm.PropertyChanged += Vm_PropertyChanged;
            // Suppress the write-back: loading the document raises TextChanged, whose
            // round-tripped text (trailing paragraph break) would otherwise mark the
            // freshly restored tab dirty and trigger a false save prompt.
            _suppressTextChange = true;
            LoadText(vm.Text);
            _suppressTextChange = false;
        }
    }

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.Text) && DataContext is TabViewModel vm)
        {
            if (!_suppressTextChange)
            {
                _suppressTextChange = true;
                LoadText(vm.Text);
                _suppressTextChange = false;
            }
        }

        if (e.PropertyName == nameof(TabViewModel.IsWordWrap) && DataContext is TabViewModel wvm)
        {
            Editor.Document.PageWidth = wvm.IsWordWrap ? double.NaN : 10000;
        }
    }

    private void LoadText(string text)
    {
        Editor.Document.Blocks.Clear();
        Editor.Document.Blocks.Add(new Paragraph(new Run(text)));
    }

    private string GetEditorText()
    {
        return new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text;
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChange) return;
        if (DataContext is not TabViewModel vm) return;
        _suppressTextChange = true;
        vm.Text = GetEditorText();
        _suppressTextChange = false;
    }

    private void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+A — select all
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Editor.SelectAll();
            e.Handled = true;
        }
    }

    // Called by MainWindow to insert ghost text (dictation preview)
    public void ShowGhostText(string text, bool isDark)
    {
        RemoveGhostText();
        if (string.IsNullOrEmpty(text)) return;

        var alpha = isDark ? 140 : 120;
        var color = isDark
            ? System.Windows.Media.Color.FromArgb((byte)alpha, 148, 185, 235)
            : System.Windows.Media.Color.FromArgb((byte)alpha, 70, 120, 190);

        var brush = new System.Windows.Media.SolidColorBrush(color) { Opacity = 0 };
        var run = new Run(text)
        {
            Foreground = brush,
            FontStyle = FontStyles.Italic,
            Tag = "ghost"
        };

        var caret = Editor.CaretPosition;
        var para = caret.Paragraph ?? (Editor.Document.Blocks.LastBlock as Paragraph);
        para?.Inlines.Add(run);

        // Quick, subtle fade-in so new preview text eases in rather than popping.
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        brush.BeginAnimation(System.Windows.Media.Brush.OpacityProperty, fade);
    }

    public void RemoveGhostText()
    {
        // Snapshot both levels first — removing from a live Inlines collection while
        // enumerating it throws "Collection was modified".
        var paragraphs = Editor.Document.Blocks.OfType<Paragraph>().ToList();
        foreach (var block in paragraphs)
        {
            var ghosts = block.Inlines.OfType<Run>().Where(r => r.Tag?.ToString() == "ghost").ToList();
            foreach (var ghost in ghosts) block.Inlines.Remove(ghost);
        }
    }

    public void FinalizeGhostText(string text)
    {
        RemoveGhostText();
        if (string.IsNullOrEmpty(text)) return;

        // Ghost text was appended at the end of the last paragraph; commit the finalized
        // text there as normal (non-ghost) text so it persists in the document.
        var para = Editor.Document.Blocks.LastBlock as Paragraph ?? new Paragraph();
        if (!Editor.Document.Blocks.Contains(para)) Editor.Document.Blocks.Add(para);
        para.Inlines.Add(new Run(text));
        Editor.CaretPosition = Editor.Document.ContentEnd;

        // Push the committed text into the view model so it autosaves. Suppress the reload
        // so setting vm.Text doesn't rebuild the document we just edited.
        if (DataContext is TabViewModel vm)
        {
            _suppressTextChange = true;
            vm.Text = GetEditorText();
            _suppressTextChange = false;
        }
    }
}
