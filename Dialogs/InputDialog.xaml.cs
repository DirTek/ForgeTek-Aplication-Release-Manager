using System.Windows;

namespace ForgeTekUpdatePackager.Dialogs;

public partial class InputDialog : Window
{
    public string Value => InputBox.Text.Trim();

    public InputDialog(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title          = title;
        TitleText.Text = title;
        PromptText.Text = prompt;
        InputBox.Text  = initialValue;
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text)) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        DialogResult ??= false;
    }
}
