using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.Dialogs;

public partial class BackupDialog : Window
{
    private readonly IBackupService _backup;
    private readonly string _rootFolder;
    private readonly string _globalSettingsFilePath;
    private readonly ObservableCollection<string> _log = [];
    private CancellationTokenSource? _cts;

    public BackupDialog(IBackupService backup, string rootFolder, string globalSettingsFilePath)
    {
        _backup = backup;
        _rootFolder = rootFolder;
        _globalSettingsFilePath = globalSettingsFilePath;
        InitializeComponent();
        LogItems.ItemsSource = _log;
        _log.CollectionChanged += (_, _) =>
            LogBorder.Visibility = _log.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var dlg = new SaveFileDialog
        {
            Title    = "Save Backup As",
            Filter   = "ZIP Archive (*.zip)|*.zip",
            FileName = $"ForgeTek_Backup_{stamp}.zip",
        };
        if (dlg.ShowDialog(this) == true)
            OutputPathBox.Text = dlg.FileName;
    }

    private async void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        var path = OutputPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            _log.Clear();
            _log.Add("Please choose an output file first.");
            return;
        }

        CreateBackupBtn.IsEnabled = false;
        BrowseBtn.IsEnabled       = false;
        _log.Clear();
        _log.Add("Starting backup…");
        _log.Add(string.Empty);

        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(msg =>
        {
            _log.Add(msg);
            LogScroller.ScrollToBottom();
        });

        try
        {
            await _backup.CreateBackupAsync(_rootFolder, _globalSettingsFilePath, path, progress, _cts.Token);
            _log.Add(string.Empty);
            _log.Add($"✔  Backup saved to: {path}");
        }
        catch (OperationCanceledException) { _log.Add("Cancelled."); }
        catch (Exception ex)              { _log.Add($"✗  {ex.Message}"); }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            CreateBackupBtn.IsEnabled = true;
            BrowseBtn.IsEnabled       = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
