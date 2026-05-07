using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Dialogs;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;
using ForgeTekUpdatePackager.Views;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class AppDetailViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly StorageService _storage;
    private readonly ScannerService _scanner;
    private readonly LogService _log;
    private readonly FtpService _ftp = new();
    private readonly UpdateCatalogService _catalog = new();
    private readonly ChangelogService _changelog = new();

    public AppEntry Entry { get; }
    public string AppName => Entry.Name;
    public string AppPath => Entry.FolderPath;
    public ObservableCollection<AppVersion> Versions { get; }

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isDiffing;
    [ObservableProperty] private ScanProgress? _scanProgress;
    [ObservableProperty] private DiffProgress? _diffProgress;

    private CancellationTokenSource? _scanCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetractVersionCommand))]
    private bool _isRetracting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ViewVersionDiffCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartPackingCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteVersionCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetractVersionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScrapVersionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReviseVersionCommand))]
    private AppVersion? _selectedVersion;

    public AppDetailViewModel(AppEntry entry, MainViewModel main, StorageService storage, ScannerService scanner, LogService log)
    {
        Entry = entry;
        _main = main;
        _storage = storage;
        _log = log;
        _scanner = scanner;
        Versions = new ObservableCollection<AppVersion>(entry.Versions.AsEnumerable().Reverse());
        CheckChangelogEntries();
    }

    private void CheckChangelogEntries()
    {
        var changelogPath = _changelog.FindChangelogFile(Entry.FolderPath);
        if (changelogPath is null) return;
        foreach (var version in Entry.Versions)
            version.HasChangelog = _changelog.HasChangelogEntry(changelogPath, version.VersionNumber);
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
        _main.RefreshSidebar(Entry);
    }

    private bool CanDeleteVersion()
    {
        if (SelectedVersion is null) return false;
        if (SelectedVersion != Entry.Versions.LastOrDefault()) return false;
        return SelectedVersion.Status != VersionStatus.Published;
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

        _log.Write("Retract", $"Retraction started — {Entry.Name} v{v.VersionNumber}");

        IsRetracting = true;
        try
        {
            // Task.Run keeps FTP continuations on pool threads, preventing
            // synchronous Dispose() from blocking the UI thread via WPF's SynchronizationContext.
            await Task.Run(() => RetractFtpAsync(v, hasPrevUpdate ? prevVersion!.VersionNumber : null));
        }
        finally
        {
            IsRetracting = false;
        }

        // Delete local version folder
        if (!string.IsNullOrWhiteSpace(v.PackagePath))
        {
            var dir = Path.GetDirectoryName(v.PackagePath);
            if (dir is not null && Directory.Exists(dir))
                try
                {
                    Directory.Delete(dir, recursive: true);
                    _log.Write("Retract", $"Local folder deleted: {dir}");
                }
                catch (Exception ex) { _log.Write("Retract", $"Local folder deletion failed: {ex.Message}"); }
        }

        _log.Write("Retract", $"Retraction complete — {Entry.Name} v{v.VersionNumber}");

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

        // Force in-place refresh — AppVersion isn't observable so the row won't update otherwise
        var vIdx = Versions.IndexOf(v);
        if (vIdx >= 0) { Versions.RemoveAt(vIdx); Versions.Insert(vIdx, v); }

        _main.RefreshSidebar(Entry);
        SelectedVersion = null; // triggers NotifyCanExecuteChanged for all version commands
    }

    private async Task RetractFtpAsync(AppVersion v, string? rollbackToVersion)
    {
        if (string.IsNullOrWhiteSpace(v.FtpHost))
        {
            _log.Write("Retract", "No FTP host — skipping remote operations");
            return;
        }

        var host     = v.FtpHost;
        var port     = v.FtpPort == 0 ? 21 : v.FtpPort;
        var user     = v.FtpUsername ?? string.Empty;
        var pass     = v.FtpPassword ?? string.Empty;
        var progress = new Progress<string>(_ => { });

        // Roll back the catalog to point at the previous version (or delete it if none remain)
        if (!string.IsNullOrWhiteSpace(v.FtpCatalogRemotePath))
        {
            _log.Write("Retract", $"Downloading catalog: {v.FtpCatalogRemotePath}");
            var existingJson = await _ftp.TryDownloadStringAsync(v.FtpCatalogRemotePath, host, port, user, pass);
            var appKey       = Entry.Name.ToLowerInvariant().Replace(" ", "");
            var updatedJson  = existingJson is not null
                ? _catalog.RemoveVersion(appKey, v.VersionNumber, existingJson, rollbackToVersion)
                : null;
            try
            {
                if (updatedJson is not null)
                {
                    await _ftp.UploadStringAsync(updatedJson, v.FtpCatalogRemotePath, host, port, user, pass);
                    _log.Write("Retract", $"Catalog rolled back: {v.FtpCatalogRemotePath}");
                }
                else
                {
                    await _ftp.DeleteFilesAsync([v.FtpCatalogRemotePath], host, port, user, pass, progress);
                    _log.Write("Retract", $"Catalog deleted (no remaining versions): {v.FtpCatalogRemotePath}");
                }
            }
            catch (Exception ex) { _log.Write("Retract", $"Catalog operation failed: {ex.Message}"); }
        }

        // Delete the package file and its remote version folder from FTP
        if (!string.IsNullOrWhiteSpace(v.FtpPackageRemotePath))
        {
            try
            {
                await _ftp.DeleteFilesAsync([v.FtpPackageRemotePath], host, port, user, pass, progress);
                _log.Write("Retract", $"Package deleted: {v.FtpPackageRemotePath}");
            }
            catch (Exception ex) { _log.Write("Retract", $"Package deletion failed: {ex.Message}"); }

            var lastSlash = v.FtpPackageRemotePath.LastIndexOf('/');
            if (lastSlash > 0)
            {
                var remoteVersionFolder = v.FtpPackageRemotePath[..lastSlash];
                try
                {
                    await _ftp.DeleteDirectoryAsync(remoteVersionFolder, host, port, user, pass);
                    _log.Write("Retract", $"Remote version folder deleted: {remoteVersionFolder}");
                }
                catch (Exception ex) { _log.Write("Retract", $"Remote folder deletion failed: {ex.Message}"); }
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanReviseVersion))]
    private void ReviseVersion()
        => _main.NavigateToRevise(Entry, SelectedVersion!);

    private bool CanReviseVersion()
        => SelectedVersion is not null
           && !SelectedVersion.IsInitial
           && SelectedVersion.Status != VersionStatus.Published
           && SelectedVersion.Status != VersionStatus.Scrapped;

    [RelayCommand(CanExecute = nameof(CanScrapVersion))]
    private void ScrapVersion()
    {
        var v = SelectedVersion!;
        if (!Confirm("Scrap Version",
                $"Permanently scrap v{v.VersionNumber}? This cannot be undone.",
                "Scrap")) return;

        v.Status = VersionStatus.Scrapped;
        _storage.Update(Entry);

        var vIdx = Versions.IndexOf(v);
        if (vIdx >= 0) { Versions.RemoveAt(vIdx); Versions.Insert(vIdx, v); }

        _main.RefreshSidebar(Entry);
        SelectedVersion = null;
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

        _scanCts   = new CancellationTokenSource();
        ScanProgress = new ScanProgress(0, 0, string.Empty);
        IsScanning = true;

        IReadOnlyList<FileRecord> files;
        try
        {
            var scanProgress = new Progress<ScanProgress>(p => ScanProgress = p);
            files = await Task.Run(
                () => _scanner.ScanDirectory(Entry.FolderPath, scanProgress, _scanCts.Token),
                _scanCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Alert("Scan Error", ex.Message);
            return;
        }
        finally
        {
            IsScanning   = false;
            ScanProgress = null;
            _scanCts.Dispose();
            _scanCts = null;
        }

        // ── EXE version detection ─────────────────────────────────────────
        string? detectedVersion = ScannerService.DetectExeVersion(Entry.FolderPath, Entry.Name);

        if (detectedVersion is null)
        {
            // Multiple EXEs exist but none matched the app name — ask the user to pick one.
            var exes = ScannerService.FindRootExeFiles(Entry.FolderPath);
            if (exes.Count > 1)
            {
                var ofd = new OpenFileDialog
                {
                    Title            = $"Select the main executable for '{Entry.Name}'",
                    Filter           = "Executable files (*.exe)|*.exe",
                    InitialDirectory = Entry.FolderPath,
                };
                if (ofd.ShowDialog() == true)
                    detectedVersion = ScannerService.ReadExeVersion(ofd.FileName);
            }
        }

        // Warn if the detected EXE version is already saved (would create a duplicate)
        if (detectedVersion is not null &&
            Entry.Versions.Any(v => string.Equals(v.VersionNumber, detectedVersion, StringComparison.OrdinalIgnoreCase)))
        {
            Alert("EXE Version Already Saved",
                $"The EXE reports v{detectedVersion}, which matches an already-saved version for this app.\n\nVerify the version number before approving.");
        }

        if (Entry.LatestVersion is { } latest)
        {
            DiffProgress = new DiffProgress("Preparing…", 0);
            IsDiffing    = true;
            DiffResult diff;
            try
            {
                var diffProgress = new Progress<DiffProgress>(p => DiffProgress = p);
                diff = await Task.Run(() => _scanner.DiffVersions(latest, files, diffProgress));
            }
            finally
            {
                IsDiffing    = false;
                DiffProgress = null;
            }

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

            _main.NavigateToDiff(Entry, files, diff, detectedVersion);
        }
        else
            _main.NavigateToScan(Entry, files, detectedVersion);
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

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

    [RelayCommand]
    private void ViewChangelog(AppVersion? version)
    {
        if (version is null) return;
        var changelogPath = _changelog.FindChangelogFile(Entry.FolderPath);
        if (changelogPath is null)
        {
            Alert("Changelog Not Found", $"No changelog.md found in:\n{Entry.FolderPath}");
            return;
        }
        var content = _changelog.ExtractVersionContent(changelogPath, version.VersionNumber);
        if (content is null)
        {
            Alert("Not Found", $"No changelog entry for v{version.VersionNumber}.");
            return;
        }
        new ChangelogView(new ChangelogViewModel(version.VersionNumber, content)).ShowDialog();
    }

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
