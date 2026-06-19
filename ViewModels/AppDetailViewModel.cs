using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;
using ForgeTekUpdatePackager.Services.Publishing;
using ForgeTekUpdatePackager.Dialogs;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class AppDetailViewModel : ObservableObject
{
    private MainViewModel _main = null!;
    private readonly IStorageService _storage;
    private readonly IScannerService _scanner;
    private readonly ILogService _log;
    private readonly IPublishService _publish;
    private readonly IUpdateCatalogService _catalog;
    private readonly IChangelogService _changelog;
    private readonly IDialogService _dialog;
    private readonly ISigningService _signing;
    private readonly ISettingsService _settings;
    private readonly ISessionService _session;
    private readonly IGitHubService _github;
    private AppEntry _entry = null!;

    public ISessionService Session => _session;
    public AppEntry Entry => _entry;
    public string AppName => _entry.Name;
    public string AppPath => _entry.FolderPath;
    public ObservableCollection<AppVersion> Versions { get; private set; } = [];

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isDiffing;
    [ObservableProperty] private ScanProgress? _scanProgress;
    [ObservableProperty] private DiffProgress? _diffProgress;

    // ── GitHub ────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildFromGitHubCommand))]
    private bool _gitHubConfigured;

    [ObservableProperty] private bool _gitHubChecking;
    [ObservableProperty] private bool _gitHubNewer;
    [ObservableProperty] private string? _gitHubStatus;
    [ObservableProperty] private string? _gitHubReleaseUrl;

    // GitHub build runner
    public ObservableCollection<string> BuildLog { get; } = [];
    private CancellationTokenSource? _buildCts;
    private string? _buildArtifactPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildFromGitHubCommand))]
    private bool _isBuilding;

    [ObservableProperty] private bool _showBuildOverlay;
    [ObservableProperty] private bool _buildFinished;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanArtifactCommand))]
    private bool _buildSucceeded;

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
        IPublishService publish, IUpdateCatalogService catalog, IChangelogService changelog, IDialogService dialog,
        ISigningService signing, ISettingsService settings, ISessionService session, IGitHubService github)
    {
        _storage = storage;
        _scanner = scanner;
        _log = log;
        _publish = publish;
        _catalog = catalog;
        _changelog = changelog;
        _dialog = dialog;
        _signing = signing;
        _settings = settings;
        _session = session;
        _github = github;
    }

    public void Initialize(AppEntry entry, MainViewModel main)
    {
        _entry = entry;
        _main = main;
        Versions = new ObservableCollection<AppVersion>(entry.Versions.AsEnumerable().Reverse());
        CheckChangelogEntries();
        RaiseWorkflowChanged();

        GitHubStatus = null;
        GitHubNewer = false;
        HasLastCommit = false;
        LastCommitSummary = string.Empty;
        LastCommitMeta = string.Empty;
        GitHubConfigured = !string.IsNullOrWhiteSpace(_settings.LoadAppSettings(entry.Name).GitHubRepo);
        if (GitHubConfigured)
        {
            CheckGitHubCommand.Execute(null);
            _ = LoadLastCommitAsync();
        }
    }

    [RelayCommand]
    private async Task CheckGitHub()
    {
        var s = _settings.LoadAppSettings(_entry.Name);
        GitHubConfigured = !string.IsNullOrWhiteSpace(s.GitHubRepo);
        if (!GitHubConfigured) return;

        GitHubChecking = true;
        GitHubNewer = false;
        GitHubStatus = "Checking GitHub…";
        try
        {
            // Per-app token overrides the account-wide connection; otherwise use the global token.
            var token = string.IsNullOrWhiteSpace(s.GitHubToken) ? _settings.Global.GitHubToken : s.GitHubToken;
            var release = await _github.GetLatestReleaseAsync(s.GitHubRepo!, token);
            if (release is null)
            {
                GitHubStatus = "No published releases on GitHub yet.";
                return;
            }
            GitHubReleaseUrl = release.HtmlUrl;
            var local = _entry.LatestVersion?.VersionNumber;
            if (string.IsNullOrEmpty(local))
            {
                GitHubStatus = $"GitHub latest release: {release.TagName} — nothing scanned yet.";
                GitHubNewer = true;
            }
            else if (GitHubService.IsNewer(release.TagName, local))
            {
                GitHubStatus = $"GitHub has {release.TagName} — newer than your scanned v{local}.";
                GitHubNewer = true;
            }
            else
            {
                GitHubStatus = $"Up to date with GitHub ({release.TagName}).";
            }
        }
        catch (Exception ex)
        {
            GitHubStatus = $"GitHub check failed: {ex.Message}";
        }
        finally { GitHubChecking = false; }
    }

    [ObservableProperty] private bool _hasLastCommit;
    [ObservableProperty] private string _lastCommitSummary = string.Empty;
    [ObservableProperty] private string _lastCommitMeta = string.Empty;

    private async Task LoadLastCommitAsync()
    {
        try
        {
            var s = _settings.LoadAppSettings(_entry.Name);
            if (string.IsNullOrWhiteSpace(s.GitHubRepo)) return;
            var token = string.IsNullOrWhiteSpace(s.GitHubToken) ? _settings.Global.GitHubToken : s.GitHubToken;
            var commit = await _github.GetLastCommitAsync(s.GitHubRepo!, token);
            if (commit is null || string.IsNullOrWhiteSpace(commit.Message)) return;
            LastCommitSummary = commit.Message;
            LastCommitMeta = commit.Meta;
            HasLastCommit = true;
        }
        catch { /* offline / no access — just leave it hidden */ }
    }

    [RelayCommand]
    private void OpenReleaseNotes()
    {
        var s = _settings.LoadAppSettings(_entry.Name);
        if (string.IsNullOrWhiteSpace(s.GitHubRepo))
        {
            _dialog.Alert("GitHub Not Configured",
                "Set this app's GitHub repository in Settings → GitHub first.");
            return;
        }
        var token = string.IsNullOrWhiteSpace(s.GitHubToken) ? _settings.Global.GitHubToken : s.GitHubToken;
        new ReleaseNotesWindow(_github, _changelog, s.GitHubRepo!, token,
            _entry.FolderPath, _entry.Name, _entry.LatestVersion?.VersionNumber)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        }.ShowDialog();
    }

    private bool CanBuildFromGitHub() => _session.CanScan && GitHubConfigured && !IsBuilding;

    [RelayCommand(CanExecute = nameof(CanBuildFromGitHub))]
    private async Task BuildFromGitHub()
    {
        var s = _settings.LoadAppSettings(_entry.Name);
        if (string.IsNullOrWhiteSpace(s.GitHubLocalPath) || string.IsNullOrWhiteSpace(s.GitHubBuildCommand))
        {
            _dialog.Alert("Build Not Configured",
                "Set the local repo path and build command in Settings → GitHub first.");
            return;
        }

        _buildArtifactPath = ResolveArtifact(s.GitHubLocalPath, s.GitHubArtifactPath);

        BuildLog.Clear();
        ShowBuildOverlay = true;
        BuildFinished = false;
        BuildSucceeded = false;
        IsBuilding = true;
        _buildCts = new CancellationTokenSource();
        var progress = new Progress<string>(line => BuildLog.Add(line));
        try
        {
            await _github.BuildAsync(s.GitHubLocalPath, s.GitHubBuildCommand, progress, _buildCts.Token);
            BuildSucceeded = true;
            BuildLog.Add(string.Empty);
            BuildLog.Add($"✔ Build complete. Artifact: {_buildArtifactPath}");
        }
        catch (OperationCanceledException) { BuildLog.Add("Cancelled."); }
        catch (Exception ex) { BuildSucceeded = false; BuildLog.Add($"✗ {ex.Message}"); }
        finally
        {
            IsBuilding = false;
            BuildFinished = true;
            _buildCts?.Dispose();
            _buildCts = null;
        }
    }

    [RelayCommand]
    private void CancelBuild() => _buildCts?.Cancel();

    [RelayCommand]
    private void CloseBuild() { ShowBuildOverlay = false; BuildFinished = false; }

    private bool CanScanArtifact() => BuildSucceeded && _buildArtifactPath is not null;

    [RelayCommand(CanExecute = nameof(CanScanArtifact))]
    private async Task ScanArtifact()
    {
        ShowBuildOverlay = false;
        BuildFinished = false;
        if (_buildArtifactPath is not null) await ScanFolder(_buildArtifactPath);
    }

    private static string ResolveArtifact(string localPath, string? artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact)) return localPath;
        return System.IO.Path.IsPathRooted(artifact) ? artifact : System.IO.Path.Combine(localPath, artifact);
    }

    // ── Workflow rail ─────────────────────────────────────────────────────
    // Frames the selected (or latest) version around its lifecycle:
    //   Scan → Review → Package → Publish.
    // The rail reflects SelectedVersion when a row is picked, otherwise the
    // latest non-scrapped version.

    /// <summary>The version the rail describes: the selected row, else the latest live one.</summary>
    public AppVersion? ActiveVersion =>
        SelectedVersion ?? _entry.Versions.LastOrDefault(v => v.Status != VersionStatus.Scrapped);

    private bool ActiveIsPublished => ActiveVersion?.Status == VersionStatus.Published;

    // Lifecycle stage index of the active version: 0=Scan 1=Review 2=Package 3=Publish.
    private int CurrentStageIndex()
    {
        var a = ActiveVersion;
        if (a is null || a.IsInitial) return 0;
        return a.Status switch
        {
            VersionStatus.Review     => 1,
            VersionStatus.Signed     => 2,
            VersionStatus.Packed     => 2,
            VersionStatus.Retracted  => 2,
            VersionStatus.Published  => 3,
            _                        => 0,
        };
    }

    private string StageState(int index)
    {
        if (ActiveIsPublished) return "Done";
        var current = CurrentStageIndex();
        return index < current ? "Done" : index == current ? "Current" : "Pending";
    }

    public string ScanState     => StageState(0);
    public string ReviewState   => StageState(1);
    public string PackageState  => StageState(2);
    public string PublishState  => StageState(3);

    public string WorkflowStageTitle =>
        ActiveIsPublished ? "Published"
        : CurrentStageIndex() switch { 1 => "Review", 2 => "Package", 3 => "Publish", _ => "Scan" };

    public string WorkflowHint
    {
        get
        {
            var a = ActiveVersion;
            if (a is null) return "Scan the app folder to register the initial version.";
            if (a.IsInitial) return "Baseline registered. Scan the folder to detect changes and start the next version.";
            return a.Status switch
            {
                VersionStatus.Published => $"v{a.VersionNumber} is live. Scan to start the next update.",
                VersionStatus.Review    => $"Changes detected for v{a.VersionNumber}. Review them, then package.",
                VersionStatus.Retracted => $"v{a.VersionNumber} was retracted. Re-package to publish it again.",
                _                       => $"Run the packaging pipeline for v{a.VersionNumber}.",
            };
        }
    }

    public string PrimaryActionText
    {
        get
        {
            var a = ActiveVersion;
            if (a is null) return "Scan Now";
            if (a.IsInitial) return "Scan for Changes";
            return a.Status switch
            {
                VersionStatus.Published => "Scan for Update",
                VersionStatus.Review    => "Review Changes",
                VersionStatus.Retracted => "Re-package",
                _                       => a.PipelineStep is not null ? "Continue Packing" : "Start Packing",
            };
        }
    }

    [RelayCommand(CanExecute = nameof(CanPrimaryAction))]
    private void PrimaryAction()
    {
        var a = ActiveVersion;
        if (a is null || a.IsInitial) { ScanNowCommand.Execute(null); return; }

        switch (a.Status)
        {
            case VersionStatus.Published:
                ScanNowCommand.Execute(null);
                break;
            case VersionStatus.Review:
                SelectedVersion = a;
                ViewVersionDiffCommand.Execute(null);
                break;
            default: // Signed / Packed / Retracted
                SelectedVersion = a;
                StartPackingCommand.Execute(null);
                break;
        }
    }

    private bool CanPrimaryAction()
    {
        var a = ActiveVersion;
        if (a is null || a.IsInitial) return _session.CanScan;            // Scan
        return a.Status switch
        {
            VersionStatus.Published => _session.CanScan,                  // Scan for update
            VersionStatus.Review    => _session.CanScan && _entry.Versions.IndexOf(a) > 0,
            VersionStatus.Scrapped  => false,
            _                       => _session.CanPublish,              // Package / publish
        };
    }

    private void RaiseWorkflowChanged()
    {
        OnPropertyChanged(nameof(ScanState));
        OnPropertyChanged(nameof(ReviewState));
        OnPropertyChanged(nameof(PackageState));
        OnPropertyChanged(nameof(PublishState));
        OnPropertyChanged(nameof(WorkflowStageTitle));
        OnPropertyChanged(nameof(WorkflowHint));
        OnPropertyChanged(nameof(PrimaryActionText));
        PrimaryActionCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedVersionChanged(AppVersion? value) => RaiseWorkflowChanged();

    private void CheckChangelogEntries()
    {
        var changelogPath = _changelog.FindChangelogFile(_entry.FolderPath);
        if (changelogPath is null) return;
        foreach (var version in _entry.Versions)
            version.HasChangelog = _changelog.HasChangelogEntry(changelogPath, version.VersionNumber);
    }

    private bool CanViewDiff()
    {
        if (!_session.CanScan) return false;
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
        if (!_session.CanPublish) return false;
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

    private bool CanSignFiles() => _session.CanPublish && SelectedVersion is not null;

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
        if (!_session.CanPublish) return false;
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
            ? $"This will delete the published files and mark v{v.VersionNumber} as retracted. The previous version (v{prevVersion!.VersionNumber}) will become the current release."
            : $"This will delete the published files and mark v{v.VersionNumber} as retracted. You can repack it later.";

        if (!_dialog.Confirm("Retract Version",
                $"Retract published version v{v.VersionNumber}?\n\n{modeText}\n\nThis cannot be undone.",
                "Retract")) return;

        _log.Write("Retract", $"Retraction started — {_entry.Name} v{v.VersionNumber}");

        IsRetracting = true;
        try
        {
            await RetractRemoteAsync(v, hasPrevUpdate ? prevVersion!.VersionNumber : null);
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
        v.PublishProvider      = null;
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

    private async Task RetractRemoteAsync(AppVersion v, string? rollbackToVersion)
    {
        var s = _settings.LoadAppSettings(_entry.Name);
        if (!_publish.IsConfigured(s))
        {
            _log.Write("Retract", "No publish target configured — skipping remote operations");
            return;
        }

        var appKey          = _entry.Name.ToLowerInvariant().Replace(" ", "");
        var catalogFileName = $"{appKey}.json";
        var packageFileName = !string.IsNullOrWhiteSpace(v.PackagePath)
            ? Path.GetFileName(v.PackagePath!)
            : Path.GetFileName(v.FtpPackageRemotePath ?? string.Empty);
        var progress = new Progress<string>(msg => _log.Write("Retract", msg));

        try
        {
            await _publish.RetractAsync(s, v, appKey, packageFileName, catalogFileName, rollbackToVersion, progress);
        }
        catch (Exception ex) { _log.Write("Retract", $"Remote retract failed: {ex.Message}"); }
    }

    [RelayCommand(CanExecute = nameof(CanReviseVersion))]
    private void ReviseVersion()
        => _main.NavigateToRevise(_entry, SelectedVersion!);

    private bool CanReviseVersion()
        => _session.CanPublish
           && SelectedVersion is not null
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
        => _session.CanPublish && SelectedVersion?.Status == VersionStatus.Retracted;

    private bool CanRetractVersion()
        => _session.CanPublish
           && SelectedVersion is not null
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

    private bool CanScanNow() => _session.CanScan;

    [RelayCommand(CanExecute = nameof(CanScanNow))]
    private Task ScanNow() => ScanFolder(_entry.FolderPath);

    // Scans an arbitrary folder through the normal pipeline (used by Scan Now and the GitHub build runner).
    private async Task ScanFolder(string folderPath)
    {
        if (!System.IO.Directory.Exists(folderPath))
        {
            _dialog.Alert("Scan Error", $"Folder not found:\n{folderPath}");
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
                () => _scanner.ScanDirectory(folderPath, scanProgress, _scanCts.Token),
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

        string? detectedVersion = _scanner.DetectExeVersion(folderPath, _entry.Name);

        if (detectedVersion is null)
        {
            var exes = _scanner.FindRootExeFiles(folderPath);
            if (exes.Count > 1)
            {
                var selectedPath = _dialog.OpenFile(
                    $"Select the main executable for '{_entry.Name}'",
                    "Executable files (*.exe)|*.exe");
                if (selectedPath is not null)
                    detectedVersion = _scanner.ReadExeVersion(selectedPath);
            }
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

            // Only warn about a duplicate EXE version once we know there are real
            // changes to review — otherwise it's redundant with "No Changes".
            WarnIfDuplicateExeVersion(detectedVersion);
            _main.NavigateToDiff(_entry, files, diff, detectedVersion);
        }
        else
        {
            WarnIfDuplicateExeVersion(detectedVersion);
            _main.NavigateToScan(_entry, files, detectedVersion);
        }
    }

    private void WarnIfDuplicateExeVersion(string? detectedVersion)
    {
        if (detectedVersion is not null &&
            _entry.Versions.Any(v => string.Equals(v.VersionNumber, detectedVersion, StringComparison.OrdinalIgnoreCase)))
        {
            _dialog.Alert("EXE Version Already Saved",
                $"The EXE reports v{detectedVersion}, which matches an already-saved version for this app.\n\nVerify the version number before approving.");
        }
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

    private bool CanDeleteApp() => _session.CanPublish;

    [RelayCommand(CanExecute = nameof(CanDeleteApp))]
    private void DeleteApp()
    {
        if (!_dialog.Confirm("Delete Application",
                $"Delete '{_entry.Name}' and all its version history? This cannot be undone.",
                "Delete App")) return;
        _storage.Delete(_entry.Id);
        _main.NavigateToWelcome();
    }
}
