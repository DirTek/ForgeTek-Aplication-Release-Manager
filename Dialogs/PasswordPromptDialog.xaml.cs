using System.Windows;
using System.Windows.Input;

namespace ForgeTekApplicationReleaseManager.Dialogs;

public partial class PasswordPromptDialog : Window
{
    public string EnteredPassword { get; private set; } = string.Empty;

    public PasswordPromptDialog(string title, string message)
    {
        InitializeComponent();
        Title            = title;
        TitleText.Text   = title;
        MessageText.Text = message;
        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        EnteredPassword = PasswordBox.Password;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Ok_Click(sender, e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        DialogResult ??= false;
    }
}
