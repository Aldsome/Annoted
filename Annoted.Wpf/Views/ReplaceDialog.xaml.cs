using System.Windows;

namespace Annoted.Wpf.Views;

public partial class ReplaceDialog : Window
{
    public ReplaceDialog(string initialFind)
    {
        InitializeComponent();
        FindBox.Text = initialFind;
        Loaded += (_, _) => FindBox.Focus();
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        // Single-replace: handled via MainWindow find/replace logic
        MessageBox.Show(Owner, "Replace next: not yet wired. Close and use Find Next then retype.", "Replace");
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(Owner, "Replace All: not yet wired.", "Replace");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
