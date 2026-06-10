using System.Windows;
using Annoted.Wpf.ViewModels;

namespace Annoted.Wpf.Views;

public partial class DownloadModelDialog : Window
{
    public string? ChosenModelKey { get; private set; }
    public string ChosenModelName { get; private set; } = string.Empty;

    public DownloadModelDialog(AudioSidebarViewModel vm)
    {
        InitializeComponent();

        foreach (var model in App.ModelManager!.AvailableModels)
            ModelCombo.Items.Add($"{model.Name}  ({model.Size})");
        ModelCombo.SelectedIndex = 1; // Base by default
    }

    // "Download" now just picks the model and closes; the actual download runs in the
    // background via the bottom hint bar so the UI never blocks.
    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (ModelCombo.SelectedIndex < 0) return;
        var chosen = App.ModelManager!.AvailableModels[ModelCombo.SelectedIndex];
        ChosenModelKey = chosen.Key;
        ChosenModelName = chosen.Name;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
