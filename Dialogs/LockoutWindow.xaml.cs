using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using Microsoft.Win32;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.Dialogs;

public partial class LockoutWindow : Window
{
    public LockoutWindow() => InitializeComponent();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Restore_Click(object sender, RoutedEventArgs e)
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
            using var zip = ZipFile.OpenRead(dlg.FileName);
            var entry = zip.GetEntry(UserService.UsersBackupEntry);
            if (entry is null)
            {
                ShowError("This backup doesn't contain a user database (settings/users.json). " +
                          "Use a backup created after the update.");
                return;
            }

            var dest = UserService.UsersFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);

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
