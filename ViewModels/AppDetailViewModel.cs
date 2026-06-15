using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;
using ForgeTekUpdatePackager.Dialogs;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class AppDetailViewModel : ObservableObject
{
    private MainViewModel _main = null!;
    private readonly IStorageService _storage;
    private readonly IScannerService _scanner;
    private readonly ILogService _log;
    private readonly IFtpService _ftp;
    private readonly IUpdateCatalogService _catalog;
    private readonly IChangelogService _changelog;
    private readonly IDialogService _dialog;
    private readonly ISigningService _signing;
    private readonly ISettingsService _settings;
    private AppEntry _entry = null!;

    public AppEntry Entry => _entry;
    public string AppName => _entry.Name;
    public string AppPath => _entry.FolderPath;
    public ObservableCollection<AppVersion> Versions { get; private set; } = [];

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
    [NotifyCanExecuteChangedFor(nameof(SignFilesCommand))]
    private AppVersion? _selectedVersion;

    public AppDetailViewModel(IStorageService storage, IScannerService scanner, ILogService log,
        IFtpService ftp, IUpdateCatalogService catalog, IChangelogService changelog, IDialogService dialog,
        ISigningService signing, ISettingsService settings)
    {
        _storage = storage;
        _scanner = scanner;
        _log = log;
        _ftp = ftp;
        _catalog = catalog;
        _changelog = changelog;
        _dialog = dialog;
        _signing = signing;
        _settings = settings;
    }

    public void Initialize(AppEntry entry, MainViewModel main)
    {
        _entry = entry;
        _main = main;
        Versions = new ObservableCollection<AppVersion>(entry.Versions.AsEnumerable().Reverse());
        CheckChangelogEntries();
    }

    private void CheckChangelogEntries()
    {
        var changelogPath = _changelog.FindChangelogFile(_entry.FolderPath);
        if (changelogPath is null) return;
        foreach (var version in _entry.Versions)
            version.HasChangelog = _changelog.HasChangelogEntry(changelogPath, version.VersionNumber);
    }

    private bool CanViewDiff()
    {
        if (SelectedVersion is null) return false;
        var idx = _entry.Versions.IndexOf(SelectedVersion);
        return idx > 0;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteVersion))]
    private void DeleteVersion()
    {
        if (!_dialog.Confirm("Delete Version",
                $"Delete v{SelectedVersion!.VersionNumber}? This cannot be undone.",
                "Delete Version")) return;

        _entry.Versions.Remove(SelectedVersion!);
        Versions.Remove(SelectedVersion!);
        SelectedVersion = null;
        _storage.Update(_entry);
        StartPackingCommand.NotifyCanExecuteChanged();
        RetractVersionCommand.NotifyCanExecuteChanged();
        ScrapVersionCommand.NotifyCanExecuteChanged();
        _main.RefreshSidebar(_entry);
    }

    private bool CanDeleteVersion()
    {
        if (SelectedVersion is null) return false;
        if (SelectedVersion != _entry.Versions.LastOrDefault()) return false;
        return SelectedVersion.Status != VersionStatus.Published;
    }

    [RelayCommand(CanExecute = nameof(CanSignFiles))]
    private void SignFiles()
    {
        var v = SelectedVersion!;
        new SignAppFilesDialog(_entry, $"v{v.VersionNumber}", v.Files, _signing, _settings, _storage)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        }.ShowDialog();
    }

    private bool CanSignFiles() => SelectedVersion is not null;

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
            var resumeData = _dialog.ShowResumePackagingDialog(v.VersionNumber, stepLabel);
            if (resumeData is null) return;

            var startStep = resumeData.StartOver
                ? PackageStep.Sign
                : v.PipelineStep!.Value + 1;

            _main.NavigateToPackage(_entry, v, startFrom: startStep);
        }
        else
        {
            _main.NavigateToPackage(_entry, v);
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

    [RelayCommand(CanExecute = nameof(CanRetractVersion))]
    private async Task RetractVersionAsync()
    {
        var v = SelectedVersion!;

        var idx          = _entry.Versions.IndexOf(v);
        var prevVersion  = idx > 1 ? _entry.Versions[idx - 1] : null;
        var hasPrevUpdate = prevVersion is not null && !prevVersion.IsInitial;

        var modeText = hasPrevUpdate
            ? $"This will delete the uploaded files from FTP and mark v{v.VersionNumber} as retracted. The previous version (v{prevVersion!.VersionNumber}) will become the current release."
            : $"This will delete the uploaded files from FTP and mark v{v.VersionNumber} as retracted. You can repack it later.";

        if (!_dialog.Confirm("Retract Version",
                $"Retract published version v{v.VersionNumber}?\n\n{modeText}\n\nThis cannot be undone.",
                "Retract")) return;

        _log.Write("Retract", $"Retraction started — {_entry.Name} v{v.VersionNumber}");

        IsRetracting = true;
        try
        {
            await Task.Run(() => RetractFtpAsync(v, hasPrevUpdate ? prevVersion!.VersionNumber : null));
        }
        finally
        {
            IsRetracting = false;
        }

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

        _log.Write("Retract", $"Retraction complete — {_entry.Name} v{v.VersionNumber}");

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

        _storage.Update(_entry);

        var vIdx = Versions.IndexOf(v);
        if (vIdx >= 0) { Versions.RemoveAt(vIdx); Versions.Insert(vIdx, v); }

        _main.RefreshSidebar(_entry);
        SelectedVersion = null;
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

        if (!string.IsNullOrWhiteSpace(v.FtpCatalogRemotePath))
        {
            _log.Write("Retract", $"Downloading catalog: {v.FtpCatalogRemotePath}");
            var existingJson = await _ftp.TryDownloadStringAsync(v.FtpCatalogRemotePath, host, port, user, pass);
            var appKey       = _entry.Name.ToLowerInvariant().Replace(" ", "");
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
        => _main.NavigateToRevise(_entry, SelectedVersion!);

    private bool CanReviseVersion()
        => SelectedVersion is not null
           && !SelectedVersion.IsInitial
           && SelectedVersion.Status != VersionStatus.Published
           && SelectedVersion.Status != VersionStatus.Scrapped;

    [RelayCommand(CanExecute = nameof(CanScrapVersion))]
    private void ScrapVersion()
    {
        var v = SelectedVersion!;
        if (!_dialog.Confirm("Scrap Version",
                $"Permanently scrap v{v.VersionNumber}? This cannot be undone.",
                "Scrap")) return;

        v.Status = VersionStatus.Scrapped;
        _storage.Update(_entry);

        var vIdx = Versions.IndexOf(v);
        if (vIdx >= 0) { Versions.RemoveAt(vIdx); Versions.Insert(vIdx, v); }

        _main.RefreshSidebar(_entry);
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
        var idx = _entry.Versions.IndexOf(SelectedVersion!);
        var baseVersion = _entry.Versions[idx - 1];
        var diff = _scanner.DiffVersions(baseVersion, SelectedVersion!.Files);
        _main.NavigateToVersionDiff(_entry, SelectedVersion!, diff);
    }

    [RelayCommand]
    private async Task ScanNow()
    {
        if (!System.IO.Directory.Exists(_entry.FolderPath))
        {
            _dialog.Alert("Scan Error", $"Folder not found:\n{_entry.FolderPath}");
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
                () => _scanner.ScanDirectory(_entry.FolderPath, scanProgress, _scanCts.Token),
                _scanCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _dialog.Alert("Scan Error", ex.Message);
            return;
        }
        finally
        {
            IsScanning   = false;
            ScanProgress = null;
            _scanCts.Dispose();
            _scanCts = null;
        }

        string? detectedVersion = _scanner.DetectExeVersion(_entry.FolderPath, _entry.Name);

        if (detectedVersion is null)
        {
            var exes = _scanner.FindRootExeFiles(_entry.FolderPath);
            if (exes.Count > 1)
            {
                var selectedPath = _dialog.OpenFile(
                    $"Select the main executable for '{_entry.Name}'",
                    "Executable files (*.exe)|*.exe");
                if (selectedPath is not null)
                    detectedVersion = _scanner.ReadExeVersion(selectedPath);
            }
        }

        if (detectedVersion is not null &&
            _entry.Versions.Any(v => string.Equals(v.VersionNumber, detectedVersion, StringComparison.OrdinalIgnoreCase)))
        {
            _dialog.Alert("EXE Version Already Saved",
                $"The EXE reports v{detectedVersion}, which matches an already-saved version for this app.\n\nVerify the version number before approving.");
        }

        if (_entry.LatestVersion is { } latest)
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
                NoChanges();
                return;
            }

            _main.NavigateToDiff(_entry, files, diff, detectedVersion);
        }
        else
            _main.NavigateToScan(_entry, files, detectedVersion);
    }

    private void NoChanges()
    {
        var dlg = new System.Windows.Window
        {
            Title = "No Changes Detected",
            SizeToContent = System.Windows.SizeToContent.WidthAndHeight,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            WindowStyle = System.Windows.WindowStyle.ToolWindow,
            ShowInTaskbar = false,
            Content = new NoChangesDialog(),
            Owner = System.Windows.Application.Current.MainWindow,
        };
        dlg.ShowDialog();
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    [RelayCommand]
    private void EditApp()
    {
        var result = _dialog.ShowAddEditApp(_entry.Name, _entry.FolderPath, _entry.AccentColor);
        if (result is null) return;
        _entry.Name = result.Name;
        _entry.FolderPath = result.Path;
        _entry.AccentColor = result.AccentColor;
        _main.NavigateToDetail(_entry);
    }

    [RelayCommand]
    private void OpenAppSettings() => _main.NavigateToAppSettings(_entry);

    [RelayCommand]
    private void ViewChangelog(AppVersion? version)
    {
        if (version is null) return;
        var changelogPath = _changelog.FindChangelogFile(_entry.FolderPath);
        if (changelogPath is null)
        {
            _dialog.Alert("Changelog Not Found", $"No changelog.md found in:\n{_entry.FolderPath}");
            return;
        }
        var content = _changelog.ExtractVersionContent(changelogPath, version.VersionNumber);
        if (content is null)
        {
            _dialog.Alert("Not Found", $"No changelog entry for v{version.VersionNumber}.");
            return;
        }
        _dialog.ShowChangelogWindow(version.VersionNumber, content);
    }

    [RelayCommand]
    private void DeleteApp()
    {
        if (!_dialog.Confirm("Delete Application",
                $"Delete '{_entry.Name}' and all its version history? This cannot be undone.",
                "Delete App")) return;
        _storage.Delete(_entry.Id);
        _main.NavigateToWelcome();
    }
}
