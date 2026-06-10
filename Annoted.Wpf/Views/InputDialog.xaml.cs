using System.Windows;
using System.Windows.Input;

namespace Annoted.Wpf.Views;

public partial class InputDialog : Window
{
    public string? Result { get; private set; }

    public InputDialog(string title, string prompt, string defaultValue)
    {
        InitializeComponent();
        Title = title;
        PromptLabel.Text = prompt;
        InputBox.Text = defaultValue;
        InputBox.SelectAll();
        Loaded += (_, _) => InputBox.Focus();
    }

    public bool? ShowDialog(Window owner) { Owner = owner; return ShowDialog(); }

    private void Ok_Click(object sender, RoutedEventArgs e) { Result = InputBox.Text; DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Result = InputBox.Text; DialogResult = true; }
    }
}
