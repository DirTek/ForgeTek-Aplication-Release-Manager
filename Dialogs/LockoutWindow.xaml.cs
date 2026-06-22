using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using ForgeTekApplicationReleaseManager.Services;

namespace ForgeTekApplicationReleaseManager.Dialogs;

public partial class LockoutWindow : Window
{
    private readonly IBackupService _backup;

    public LockoutWindow(IBackupService backup)
    {
        _backup = backup;
        InitializeComponent();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title           = "Select a backup ZIP that contains your users",
            Filter          = "Backup ZIP (*.zip)|*.zip|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var progress = new Progress<string>(_ => { });
            var restoredUsers = await _backup.RestoreAsync(dlg.FileName, progress, CancellationToken.None);
            if (restoredUsers == 0)
            {
                ShowError("This backup doesn't contain a user database. " +
                          "Use a backup created after the update.");
                return;
            }

            // Relaunch into the normal login flow.
            var exe = Environment.ProcessPath;
            if (exe is not null) Process.Start(exe);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            ShowError($"Restore failed: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
