using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Annoted.Wpf.ViewModels;

namespace Annoted.Wpf.Views;

public partial class AudioSidebarView : UserControl
{
    private System.Windows.Threading.DispatcherTimer? _renderTimer;

    public AudioSidebarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is AudioSidebarViewModel oldVm)
            oldVm.PropertyChanged -= Vm_PropertyChanged;
        if (e.NewValue is AudioSidebarViewModel newVm)
            newVm.PropertyChanged += Vm_PropertyChanged;
    }

    // Eased level the shape actually renders at — chases the VM's raw level for fluidity.
    private double _displayLevel;

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AudioSidebarViewModel.IsVisualizerActive))
        {
            if (DataContext is AudioSidebarViewModel vm && vm.IsVisualizerActive)
                StartVisualizer();
            else
                StopVisualizer();
        }
        else if (e.PropertyName is nameof(AudioSidebarViewModel.PlaybackRatio))
        {
            PositionPlayhead(((AudioSidebarViewModel)DataContext).PlaybackRatio);
        }
        else if (e.PropertyName is nameof(AudioSidebarViewModel.IsPlaying))
        {
            if (DataContext is AudioSidebarViewModel v && v.IsPlaying) StartPlayhead();
            else StopPlayhead();
        }
    }

    private System.Windows.Threading.DispatcherTimer? _playheadTimer;

    private void StartPlayhead()
    {
        _playheadTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _playheadTimer.Tick -= PlayheadTick;
        _playheadTimer.Tick += PlayheadTick;
        _playheadTimer.Start();
    }

    private void StopPlayhead()
    {
        _playheadTimer?.Stop();
        if (DataContext is AudioSidebarViewModel vm) PositionPlayhead(vm.PlaybackRatio); // keep pending position
    }

    private void PlayheadTick(object? sender, EventArgs e)
    {
        if (DataContext is AudioSidebarViewModel vm) PositionPlayhead(vm.GetLivePlaybackRatio());
    }

    private void PositionPlayhead(double ratio)
    {
        var w = PlayheadCanvas.ActualWidth;
        var hgt = PlayheadCanvas.ActualHeight;
        if (ratio > 0 && w > 0 && DataContext is AudioSidebarViewModel vm && !vm.IsVisualizerActive)
        {
            var x = Math.Clamp(ratio, 0, 1) * w;
            Playhead.X1 = x; Playhead.X2 = x; Playhead.Y2 = hgt;
            Playhead.Visibility = Visibility.Visible;
        }
        else
        {
            Playhead.Visibility = Visibility.Collapsed;
        }
    }

    private void MemoList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is AudioSidebarViewModel vm) vm.OpenSelectedMemoTab();
    }

    private System.Collections.Generic.List<MemoListItem> SelectedItems()
        => MemoList.SelectedItems.Cast<MemoListItem>().ToList();

    private AudioSidebarViewModel? Vm => DataContext as AudioSidebarViewModel;

    private void MemoList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case System.Windows.Input.Key.Delete: Ctx_Delete(sender, e); e.Handled = true; break;
            case System.Windows.Input.Key.F2:     Ctx_Rename(sender, e); e.Handled = true; break;
            case System.Windows.Input.Key.E when ctrl: Ctx_Export(sender, e); e.Handled = true; break;
            case System.Windows.Input.Key.Space:  Vm?.PlaySelectedCommand.Execute(null); e.Handled = true; break;
            case System.Windows.Input.Key.S when ctrl: Vm?.StopAudioCommand.Execute(null); e.Handled = true; break;
        }
    }

    private void Ctx_OpenTab(object sender, RoutedEventArgs e) => Vm?.OpenSelectedMemoTab();

    private void Ctx_OpenLocation(object sender, RoutedEventArgs e) => Vm?.OpenLocationCommand.Execute(null);

    private void Ctx_Rename(object sender, RoutedEventArgs e)
    {
        var items = SelectedItems();
        if (Vm is null || items.Count == 0) return;
        var def = items.Count == 1 ? items[0].DisplayName : "Memoir";
        var dlg = new InputDialog(items.Count > 1 ? "Batch Rename" : "Rename Memoir",
            items.Count > 1 ? "Base name (numbered):" : "Memoir name:", def) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Result))
            Vm.RenameMemos(items, dlg.Result.Trim());
    }

    private void Ctx_Export(object sender, RoutedEventArgs e)
    {
        var items = SelectedItems();
        if (Vm is null || items.Count == 0) return;
        using var fb = new System.Windows.Forms.FolderBrowserDialog { Description = "Export selected Memoir(s) to folder" };
        if (fb.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            Vm.ExportMemos(items, fb.SelectedPath);
    }

    private void Ctx_Move(object sender, RoutedEventArgs e)
    {
        var items = SelectedItems();
        if (Vm is null || items.Count == 0) return;
        Vm.MoveMemosToActiveTab(items);
    }

    private void Ctx_Delete(object sender, RoutedEventArgs e)
    {
        var items = SelectedItems();
        if (Vm is null || items.Count == 0) return;
        var msg = items.Count == 1
            ? $"Delete \"{items[0].DisplayName}\"? This removes the recording file."
            : $"Delete {items.Count} Memoirs? This removes their recording files.";
        if (MessageBox.Show(Window.GetWindow(this)!, msg, "Delete Memoir",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            Vm.DeleteMemos(items);
    }

    private System.Windows.Point _downPoint;
    private bool _doubleClickDown;   // true while a double-click's button is held — never scrub/pause
    private bool _isScrubbing;       // true once movement passes the drag threshold

    // Click count decides intent immediately — no OS double-click interval delay.
    //  • single-click → seek (pause+seek if playing)
    //  • double-click → seek + play from the exact position
    private void Waveform_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not AudioSidebarViewModel vm || vm.IsVisualizerActive) return;
        var ratio = PointerRatio(e);
        _downPoint = e.GetPosition(WaveformHost);
        _isScrubbing = false;
        _doubleClickDown = e.ClickCount >= 2;

        if (_doubleClickDown) _ = vm.PlayFromAsync(ratio);
        else vm.SetPlayheadPosition(ratio);
    }

    private void Waveform_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _doubleClickDown = false;
        _isScrubbing = false;
    }

    private void Waveform_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        if (_doubleClickDown) return;              // double-click hold is not a scrub — don't pause playback
        if (DataContext is not AudioSidebarViewModel vm || vm.IsVisualizerActive) return;

        // Ignore micro-jitter so a stationary click isn't treated as a drag.
        if (!_isScrubbing &&
            Math.Abs(e.GetPosition(WaveformHost).X - _downPoint.X) < SystemParameters.MinimumHorizontalDragDistance)
            return;
        _isScrubbing = true;
        vm.SetPlayheadPosition(PointerRatio(e));   // scrub position only, no autoplay
    }

    private double PointerRatio(System.Windows.Input.MouseEventArgs e)
    {
        var x = e.GetPosition(WaveformHost).X;
        var w = WaveformHost.ActualWidth;
        return w <= 0 ? 0 : Math.Clamp(x / w, 0, 1);
    }

    private void StartVisualizer()
    {
        _displayLevel = 0;
        _renderTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _renderTimer.Tick -= OnVisualizerTick;
        _renderTimer.Tick += OnVisualizerTick;
        _renderTimer.Start();
    }

    private void StopVisualizer()
    {
        _renderTimer?.Stop();
        DictationCanvas.Children.Clear();
    }

    private void OnVisualizerTick(object? sender, EventArgs e) => RenderVisualizer();

    // One reusable audio-reactive shape for both record + dictate.
    //  • dictation  → full blue ball
    //  • recording  → smiling crescent / half-moon
    // Both pulse from the same smoothed audio level.
    private double _morph = 1.0; // 1 = full ball (dictation), 0 = crescent (recording)

    private void RenderVisualizer()
    {
        if (DataContext is not AudioSidebarViewModel vm) return;
        DictationCanvas.Children.Clear();

        var w = DictationCanvas.ActualWidth;
        var h = DictationCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Accent follows the theme/appearance setting (AccentBrush), not a hardcoded blue.
        var accent = (Application.Current.Resources["AccentBrush"] as SolidColorBrush)?.Color
                     ?? Color.FromRgb(0, 122, 204);

        // Visual-only normalization (does NOT touch recorded/dictation audio):
        // map peak through a log curve so whispers/snaps still move the shape, no noise gate.
        var raw = Math.Clamp(vm.VisualizerLevel, 0f, 1f);
        var norm = raw <= 0 ? 0.0 : Math.Clamp(Math.Log10(1 + 9 * raw), 0, 1); // log curve

        // Reuse the existing easing (_displayLevel) — single smoothing system.
        _displayLevel += (norm - _displayLevel) * 0.35;
        var level = _displayLevel;

        // Smoothly morph shape toward the current mode.
        var morphTarget = vm.IsRecordingMode ? 0.0 : 1.0;
        _morph += (morphTarget - _morph) * 0.20;

        var cx = w / 2;
        var cy = h / 2;
        var maxR = Math.Min(cx, cy) - 8;

        // Scale caps: always-visible minimum, normalized range up to a max cap.
        const double minScale = 0.45, maxScale = 1.0;
        var radius = maxR * (minScale + (maxScale - minScale) * level);
        radius = Math.Clamp(radius, 6, maxR);

        // Soft outer glow that grows with level.
        var glowR = radius * (1.2 + 0.3 * level);
        var glowAlpha = (byte)(60 * Math.Clamp(0.5 + level, 0, 1));
        var glow = new System.Windows.Shapes.Ellipse
        {
            Width = glowR * 2, Height = glowR * 2,
            Fill = new SolidColorBrush(Color.FromArgb(glowAlpha, accent.R, accent.G, accent.B))
        };
        Canvas.SetLeft(glow, cx - glowR);
        Canvas.SetTop(glow, cy - glowR);
        DictationCanvas.Children.Add(glow);

        var fill = new SolidColorBrush(accent);
        var full = new EllipseGeometry(new System.Windows.Point(cx, cy), radius, radius);

        if (_morph > 0.985)
        {
            // Fully a ball.
            DictationCanvas.Children.Add(new System.Windows.Shapes.Path { Data = full, Fill = fill });
        }
        else
        {
            // Crescent: subtract an upward-offset disc; the offset shrinks as morph→1 (ball).
            var biteOffset = radius * 0.55 * (1 - _morph);
            var bite = new EllipseGeometry(new System.Windows.Point(cx, cy - biteOffset), radius * 0.95, radius * 0.95);
            var crescent = new CombinedGeometry(GeometryCombineMode.Exclude, full, bite);
            DictationCanvas.Children.Add(new System.Windows.Shapes.Path { Data = crescent, Fill = fill });
        }
    }
}
