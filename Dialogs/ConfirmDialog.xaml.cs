using System.Windows;

namespace ForgeTekApplicationReleaseManager.Dialogs;

public partial class ConfirmDialog : Window
{
    /// <param name="isDanger">
    /// True (default) — confirm button uses DangerButton style (red).
    /// False — uses PrimaryButton style (blue) for non-destructive confirmations.
    /// </param>
    public ConfirmDialog(string title, string message, string confirmLabel, bool isDanger = true)
    {
        InitializeComponent();
        Title              = title;
        TitleText.Text     = title;
        MessageText.Text   = message;
        ConfirmBtn.Content = confirmLabel;
        if (!isDanger)
            ConfirmBtn.Style = (Style)FindResource("PrimaryButton");
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e)  => DialogResult = false;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        DialogResult ??= false;
    }
}
