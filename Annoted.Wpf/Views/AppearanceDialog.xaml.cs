using System.Windows;
using System.Windows.Media;

namespace Annoted.Wpf.Views;

public partial class AppearanceDialog : Window
{
    public string SelectedMode { get; private set; }       // "system" | "light" | "dark"
    public Color? SelectedBack { get; private set; }       // null = theme default
    public Color? SelectedFore { get; private set; }
    public Color? SelectedAccent { get; private set; }

    /// <summary>Live preview callback: (mode, back, fore, accent).</summary>
    public Action<string, Color?, Color?, Color?>? Preview;

    private bool _loaded;

    public AppearanceDialog(string mode, Color? back, Color? fore, Color? accent)
    {
        InitializeComponent();

        SelectedMode = mode;
        SelectedBack = back;
        SelectedFore = fore;
        SelectedAccent = accent;

        SystemRadio.IsChecked = mode == "system";
        LightRadio.IsChecked = mode == "light";
        DarkRadio.IsChecked = mode == "dark";
        UpdateSwatches();
        _loaded = true;
    }

    private void UpdateSwatches()
    {
        BackSwatch.Background = Swatch(SelectedBack, "EditorBackBrush");
        ForeSwatch.Background = Swatch(SelectedFore, "ForeBrush");
        AccentSwatch.Background = Swatch(SelectedAccent, "AccentBrush");
    }

    private static System.Windows.Media.Brush Swatch(Color? c, string fallbackKey)
        => c is { } v ? new SolidColorBrush(v) : (System.Windows.Media.Brush)Application.Current.Resources[fallbackKey];

    private static Color? PickColor(Color? current)
    {
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true };
        if (current is { } c)
            dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
        return Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
    }

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        SelectedMode = DarkRadio.IsChecked == true ? "dark"
                     : LightRadio.IsChecked == true ? "light" : "system";
        DoPreview();
    }

    private void ChooseBack_Click(object sender, RoutedEventArgs e)
    {
        var p = PickColor(SelectedBack);
        if (p is not null) { SelectedBack = p; UpdateSwatches(); DoPreview(); }
    }

    private void ChooseFore_Click(object sender, RoutedEventArgs e)
    {
        var p = PickColor(SelectedFore);
        if (p is not null) { SelectedFore = p; UpdateSwatches(); DoPreview(); }
    }

    private void ChooseAccent_Click(object sender, RoutedEventArgs e)
    {
        var p = PickColor(SelectedAccent);
        if (p is not null) { SelectedAccent = p; UpdateSwatches(); DoPreview(); }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        SelectedBack = null;
        SelectedFore = null;
        SelectedAccent = null;
        UpdateSwatches();
        DoPreview();
    }

    private void DoPreview()
    {
        Preview?.Invoke(SelectedMode, SelectedBack, SelectedFore, SelectedAccent);
        UpdateSwatches();
    }

    private void Preview_Click(object sender, RoutedEventArgs e) => DoPreview();

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
