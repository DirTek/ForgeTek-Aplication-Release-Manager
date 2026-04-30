using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Dialogs;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class AppDetailViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly StorageService _storage;
    private readonly ScannerService _scanner;
    private readonly FtpService _ftp = new();

    public AppEntry Entry { get; }
    public string AppName => Entry.Name;
    public string AppPath => Entry.FolderPath;
    public ObservableCollection<AppVersion> Versions { get; }

    [ObservableProperty] private bool _isScanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetractVersionCommand))]
    private bool _isRetracting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ViewVersionDiffCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartPackingCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteVersionCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetractVersionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScrapVersionCommand))]
    private AppVersion? _selectedVersion;

    public AppDetailViewModel(AppEntry entry, MainViewModel main, StorageService storage, ScannerService scanner)
    {
        Entry = entry;
        _main = main;
        _storage = storage;
        _scanner = scanner;
        Versions = new ObservableCollection<AppVersion>(entry.Versions.AsEnumerable().Reverse());
    }

    private bool CanViewDiff()
    {
        if (SelectedVersion is null) return false;
        var idx = Entry.Versions.IndexOf(SelectedVersion);
        return idx > 0; // has a previous version to diff against
    }

    [RelayCommand(CanExecute = nameof(CanDeleteVersion))]
    private void DeleteVersion()
    {
        if (!Confirm("Delete Version",
                $"Delete v{SelectedVersion!.VersionNumber}? This cannot be undone.",
                "Delete Version")) return;

        Entry.Versions.Remove(SelectedVersion!);
        Versions.Remove(SelectedVersion!);
        SelectedVersion = null;
        _storage.Update(Entry);
        StartPackingCommand.NotifyCanExecuteChanged();
        RetractVersionCommand.NotifyCanExecuteChanged();
        ScrapVersionCommand.NotifyCanExecuteChanged();
    }

    private bool CanDeleteVersion()
    {
        if (SelectedVersion is null) return false;
        // Must be the latest version
        if (SelectedVersion != Entry.LatestVersion) return false;
        // Cannot delete a published version
        if (SelectedVersion.Status == VersionStatus.Published) return false;
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanStartPacking))]
    private void StartPacking()
    {
        var v = SelectedVersion!;
        var stepLabel = v.PipelineStep switch
        {
            PackageStep.Manifest => "Manifest",
            PackageStep.Package  => "Package",
            PackageStep.Json     => "JSON",
            PackageStep.Ftp      => "FTP",
            _                    => null,
        };

        if (stepLabel is not null)
        {
            var dlg = new ResumePackagingDialog(v.VersionNumber, stepLabel)
            {
                Owner = System.Windows.Application.Current.MainWindow,
            };

            if (dlg.ShowDialog() != true) return;

            var startStep = dlg.Choice == ResumePackagingDialog.ResumeChoice.StartOver
                ? PackageStep.Sign
                : v.PipelineStep!.Value + 1;

            _main.NavigateToPackage(Entry, v, startFrom: startStep);
        }
        else
        {
            _main.NavigateToPackage(Entry, v);
        }
    }

    private bool CanStartPacking()
    {
        if (SelectedVersion is null) return false;
        if (SelectedVersion.IsInitial) return false;
        if (SelectedVersion.Status == VersionStatus.Published) return false;
        if (SelectedVersion.Status == VersionStatus.Scrapped) return false;
        return true;
    }

    // ── Retract ────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanRetractVersion))]
    private async Task RetractVersionAsync()
    {
        var v = SelectedVersion!;

        // Determine retract mode before asking the user
        var idx          = Entry.Versions.IndexOf(v);
        var prevVersion  = idx > 1 ? Entry.Versions[idx - 1] : null; // idx-1 is initial when idx==1
        var hasPrevUpdate = prevVersion is not null && !prevVersion.IsInitial;

        var modeText = hasPrevUpdate
            ? $"This will delete the uploaded files from FTP and mark v{v.VersionNumber} as retracted. The previous version (v{prevVersion!.VersionNumber}) will become the current release."
            : $"This will delete the uploaded files from FTP and mark v{v.VersionNumber} as retracted. You can repack it later.";

        if (!Confirm("Retract Version",
                $"Retract published version v{v.VersionNumber}?\n\n{modeText}\n\nThis cannot be undone.",
                "Retract")) return;

        IsRetracting = true;
        try
        {
            await Task.Run(() => DeleteFtpFilesAsync(v));
        }
        finally
        {
            IsRetracting = false;
        }

        // Mark as retracted and clear publish data — version stays in the list.
        v.Status               = VersionStatus.Retracted;
        v.HasManifest          = false;
        v.HasPackage           = false;
        v.PackagePath          = null;
        v.PipelineStep         = null;
        v.FtpPackageRemotePath = null;
        v.FtpCatalogRemotePath = null;
        v.FtpHost              = null;
        v.FtpPort              = 0;
        v.FtpUsername          = null;
        v.FtpPassword          = null;

        _storage.Update(Entry);
        StartPackingCommand.NotifyCanExecuteChanged();
        RetractVersionCommand.NotifyCanExecuteChanged();
        ScrapVersionCommand.NotifyCanExecuteChanged();
    }

    private async Task DeleteFtpFilesAsync(AppVersion v)
    {
        if (string.IsNullOrWhiteSpace(v.FtpHost)) return;

        var toDelete = new List<string>();
        if (!string.IsNullOrWhiteSpace(v.FtpPackageRemotePath))
            toDelete.Add(v.FtpPackageRemotePath);
        if (!string.IsNullOrWhiteSpace(v.FtpCatalogRemotePath))
            toDelete.Add(v.FtpCatalogRemotePath);

        if (toDelete.Count == 0) return;

        // Progress is fire-and-forget here (retract is a background op without a log panel)
        var progress = new Progress<string>(_ => { });
        try
        {
            await _ftp.DeleteFilesAsync(toDelete,
                v.FtpHost, v.FtpPort == 0 ? 21 : v.FtpPort,
                v.FtpUsername ?? string.Empty, v.FtpPassword ?? string.Empty,
                progress);
        }
        catch { /* best-effort — files may already be gone */ }
    }

    [RelayCommand(CanExecute = nameof(CanScrapVersion))]
    private void ScrapVersion()
    {
        var v = SelectedVersion!;
        if (!Confirm("Scrap Version",
                $"Permanently scrap v{v.VersionNumber}? This cannot be undone.",
                "Scrap")) return;

        v.Status = VersionStatus.Scrapped;
        _storage.Update(Entry);
        StartPackingCommand.NotifyCanExecuteChanged();
        RetractVersionCommand.NotifyCanExecuteChanged();
        ScrapVersionCommand.NotifyCanExecuteChanged();
    }

    private bool CanScrapVersion()
        => SelectedVersion?.Status == VersionStatus.Retracted;

    private bool CanRetractVersion()
        => SelectedVersion is not null
           && SelectedVersion.Status == VersionStatus.Published
           && !IsRetracting;

    [RelayCommand(CanExecute = nameof(CanViewDiff))]
    private void ViewVersionDiff()
    {
        var idx = Entry.Versions.IndexOf(SelectedVersion!);
        var baseVersion = Entry.Versions[idx - 1];
        var diff = _scanner.DiffVersions(baseVersion, SelectedVersion!.Files);
        _main.NavigateToVersionDiff(Entry, SelectedVersion!, diff);
    }

    [RelayCommand]
    private async Task ScanNow()
    {
        if (!System.IO.Directory.Exists(Entry.FolderPath))
        {
            Alert("Scan Error", $"Folder not found:\n{Entry.FolderPath}");
            return;
        }

        IsScanning = true;
        IReadOnlyList<FileRecord> files;
        try
        {
            files = await Task.Run(() => _scanner.ScanDirectory(Entry.FolderPath));
        }
        catch (Exception ex)
        {
            Alert("Scan Error", ex.Message);
            IsScanning = false;
            return;
        }
        IsScanning = false;

        if (Entry.LatestVersion is { } latest)
        {
            var diff = _scanner.DiffVersions(latest, files);
            if (diff.Added.Count == 0 && diff.Modified.Count == 0)
            {
                var dlg = new Window
                {
                    Title = "No Changes Detected",
                    SizeToContent = SizeToContent.WidthAndHeight,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    WindowStyle = WindowStyle.ToolWindow,
                    ShowInTaskbar = false,
                    Content = new NoChangesDialog(),
                    Owner = System.Windows.Application.Current.MainWindow,
                };
                dlg.ShowDialog();
                return;
            }

            _main.NavigateToDiff(Entry, files, diff);
        }
        else
            _main.NavigateToScan(Entry, files);
    }

    [RelayCommand]
    private void EditApp()
    {
        var dlg = new AddEditAppDialog(Entry.Name, Entry.FolderPath)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (dlg.ShowDialog() != true) return;
        Entry.Name = dlg.AppName;
        Entry.FolderPath = dlg.AppPath;
        _main.NavigateToDetail(Entry);
    }

    [RelayCommand]
    private void OpenAppSettings() => _main.NavigateToAppSettings(Entry);

    private static bool Confirm(string title, string message, string confirmLabel)
        => new ConfirmDialog(title, message, confirmLabel)
               { Owner = System.Windows.Application.Current.MainWindow }
               .ShowDialog() == true;

    private static void Alert(string title, string message)
        => new AlertDialog(title, message)
               { Owner = System.Windows.Application.Current.MainWindow }
               .ShowDialog();

    [RelayCommand]
    private void DeleteApp()
    {
        if (!Confirm("Delete Application",
                $"Delete '{Entry.Name}' and all its version history? This cannot be undone.",
                "Delete App")) return;
        _storage.Delete(Entry.Id);
        _main.NavigateToWelcome();
    }
}
