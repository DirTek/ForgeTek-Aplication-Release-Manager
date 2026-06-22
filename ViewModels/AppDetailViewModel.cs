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
    private readonly IVulnerabilityScanService _vulnScan;
    private readonly ILicenseScanService _licenseScan;
    private readonly ISourceCompareService _sourceCompare;
    private readonly ISetupStorageService _setupStorage;
    private readonly IApprovalService _approval;
    private readonly Services.Storage.IFileBlobStore _blobs;
    private AppEntry _entry = null!;

    public ISessionService Session => _session;
    public AppEntry Entry => _entry;
    public string AppName => _entry.Name;
    public string AppPath => _entry.FolderPath;
    public ObservableCollection<AppVersion> Versions { get; private set; } = [];

    // ── Channel filter for the version list (remembered across sessions) ──────
    private string _channelFilter = "All";   // "All" | "Stable" | "Beta"
    public string ChannelFilter => _channelFilter;
    public bool IsChannelAll    => _channelFilter == "All";
    public bool IsChannelStable => _channelFilter == "Stable";
    public bool IsChannelBeta   => _channelFilter == "Beta";

    [RelayCommand]
    private void SetChannelFilter(string? mode)
    {
        var value = mode is "Stable" or "Beta" ? mode : "All";
        if (value == _channelFilter) return;
        _channelFilter = value;
        _settings.Global.VersionChannelFilter = value;
        _settings.SaveGlobal();
        OnPropertyChanged(nameof(ChannelFilter));
        OnPropertyChanged(nameof(IsChannelAll));
        OnPropertyChanged(nameof(IsChannelStable));
        OnPropertyChanged(nameof(IsChannelBeta));
        RebuildVersions();
    }

    // Rebuilds the displayed version list (newest first) honoring the channel filter.
    private void RebuildVersions()
    {
        ApplySetupDates();
        IEnumerable<AppVersion> q = _entry.Versions.AsEnumerable().Reverse();
        q = _channelFilter switch
        {
            "Stable" => q.Where(v => v.Channel == UpdateChannel.Stable),
            "Beta"   => q.Where(v => v.Channel == UpdateChannel.Beta),
            _        => q,
        };
        Versions = new ObservableCollection<AppVersion>(q);
        OnPropertyChanged(nameof(Versions));
    }

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isDiffing;
    [ObservableProperty] private ScanProgress? _scanProgress;
    [ObservableProperty] private DiffProgress? _diffProgress;

    // ── GitHub ────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildFromGitHubCommand))]
    [NotifyPropertyChangedFor(nameof(HasScanSource))]
    private bool _gitHubConfigured;

    /// <summary>This app has a solution/source path configured (for source builds + scanning).</summary>
    public bool HasSolution => !string.IsNullOrWhiteSpace(_entry?.SolutionPath);
    /// <summary>A source the scanners can use exists (a solution path or a GitHub connection).</summary>
    public bool HasScanSource => HasSolution || GitHubConfigured;
    public string SolutionPathDisplay => _entry?.SolutionPath ?? string.Empty;

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
    [NotifyCanExecuteChangedFor(nameof(BuildFromSolutionCommand))]
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
        ISigningService signing, ISettingsService settings, ISessionService session, IGitHubService github,
        IVulnerabilityScanService vulnScan, ILicenseScanService licenseScan,
        ISourceCompareService sourceCompare, ISetupStorageService setupStorage, IApprovalService approval,
        Services.Storage.IFileBlobStore blobs)
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
        _vulnScan = vulnScan;
        _licenseScan = licenseScan;
        _sourceCompare = sourceCompare;
        _setupStorage = setupStorage;
        _approval = approval;
        _blobs = blobs;
    }

    // ── Release approvals (Admin + QA Tester sign-off on the selected version) ──
    public ObservableCollection<string> ApprovalThread { get; } = [];
    [ObservableProperty] private string _approvalSummary = string.Empty;
    [ObservableProperty] private string _reviewNoteDraft = string.Empty;

    /// <summary>Show the approvals panel only when the feature is enabled, protection is on, and a
    /// non-initial version is selected.</summary>
    public bool ShowApprovalPanel =>
        _settings.Global.RequireReleaseApproval && _session.IsProtected
        && SelectedVersion is { IsInitial: false };

    /// <summary>Admin / QA Tester may cast a binding vote.</summary>
    public bool CanCastVote => _session.CanApprove;

    private string? SelectedApprovalKey =>
        SelectedVersion is null ? null : ReleaseApproval.ForApp(_entry.Id, SelectedVersion.VersionNumber);

    private void RefreshApprovalPanel()
    {
        ApprovalThread.Clear();
        OnPropertyChanged(nameof(ShowApprovalPanel));
        OnPropertyChanged(nameof(CanCastVote));
        ApproveSelectedCommand.NotifyCanExecuteChanged();
        RejectSelectedCommand.NotifyCanExecuteChanged();
        AddReviewNoteCommand.NotifyCanExecuteChanged();

        if (SelectedApprovalKey is not { } key) { ApprovalSummary = string.Empty; return; }

        var votes = _approval.GetForTarget(key);
        foreach (var v in votes)
        {
            var verb = v.Decision switch
            {
                ApprovalDecision.Approve => "approved",
                ApprovalDecision.Reject  => "rejected",
                _                        => "noted",
            };
            var note = string.IsNullOrWhiteSpace(v.Note) ? "" : $" — “{v.Note}”";
            ApprovalThread.Add($"{v.ByUser} ({v.ByRole}) {verb} · {v.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm}{note}");
        }

        var state = ApprovalService.Evaluate(votes);
        var satisfied = ApprovalService.CountSatisfied(votes);
        ApprovalSummary = state switch
        {
            ApprovalState.Approved => "Approved for release (Admin + QA Tester).",
            ApprovalState.Rejected => "Rejected — needs changes and re-approval.",
            _                      => $"Pending — {satisfied} of 2 approvals (Admin + QA Tester).",
        };
    }

    private bool CanVoteOnSelected() => _session.CanApprove && SelectedApprovalKey is not null;

    [RelayCommand(CanExecute = nameof(CanVoteOnSelected))]
    private void ApproveSelected() => CastVote(ApprovalDecision.Approve);

    [RelayCommand(CanExecute = nameof(CanVoteOnSelected))]
    private void RejectSelected()
    {
        if (string.IsNullOrWhiteSpace(ReviewNoteDraft))
        {
            _dialog.Alert("Note Required", "Add a note explaining the rejection before rejecting.");
            return;
        }
        CastVote(ApprovalDecision.Reject);
    }

    private bool CanAddReviewNote() => _session.CanReviewNote && SelectedApprovalKey is not null
                                       && !string.IsNullOrWhiteSpace(ReviewNoteDraft);

    [RelayCommand(CanExecute = nameof(CanAddReviewNote))]
    private void AddReviewNote() => CastVote(ApprovalDecision.Note);

    private void CastVote(ApprovalDecision decision)
    {
        if (SelectedApprovalKey is not { } key || SelectedVersion is null) return;
        _approval.Add(new ReleaseApproval
        {
            TargetKey    = key,
            Decision     = decision,
            Note         = string.IsNullOrWhiteSpace(ReviewNoteDraft) ? null : ReviewNoteDraft.Trim(),
            ByUser       = _session.ActorName,
            ByRole       = _session.Current?.Role ?? UserRole.Admin,
            TimestampUtc = DateTime.UtcNow,
        });
        ReviewNoteDraft = string.Empty;
        _log.Write("Approval", $"{_session.ActorName} {decision} {_entry.Name} v{SelectedVersion.VersionNumber}");
        RefreshApprovalPanel();
    }

    // Stamps each version with the most recent date a setup bundle that shipped it was generated,
    // by joining the app's version numbers against the setup history's per-app version snapshots.
    private void ApplySetupDates()
    {
        var dates = _setupStorage.GetHistory()
            .Where(r => r.AppVersions.TryGetValue(_entry.Id, out var v) && !string.IsNullOrEmpty(v))
            .GroupBy(r => r.AppVersions[_entry.Id])
            .ToDictionary(g => g.Key, g => g.Max(r => r.GeneratedDate), StringComparer.OrdinalIgnoreCase);

        foreach (var version in _entry.Versions)
            version.SetupGeneratedDate =
                dates.TryGetValue(version.VersionNumber, out var d) ? d : null;
    }

    /// <summary>Opens a 3-way compare of the app's files, its solution build output, and its GitHub
    /// build output (SLN = files = GitHub).</summary>
    [RelayCommand]
    private void CompareSources()
    {
        var s = _settings.LoadAppSettings(_entry.Name);

        string? slnPublish = null;
        if (!string.IsNullOrWhiteSpace(_entry.SolutionPath)
            && ProjectLocator.Resolve(_entry.SolutionPath) is { } target)
        {
            slnPublish = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target)!, "ForgeTekPublish");
        }

        // GitHub build output: the configured artifact path, or the local clone. Either being set is
        // enough (an absolute artifact path needs no clone), so connected repos pre-fill without a clone.
        string? githubArtifact = null;
        if (!string.IsNullOrWhiteSpace(s.GitHubArtifactPath) || !string.IsNullOrWhiteSpace(s.GitHubLocalPath))
            githubArtifact = ResolveArtifact(s.GitHubLocalPath ?? string.Empty, s.GitHubArtifactPath);

        new SourceCompareWindow(_sourceCompare, _entry.Name, _entry.FolderPath, slnPublish, githubArtifact)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        }.ShowDialog();
    }

    /// <summary>Compares the app's local project SOURCE against the connected GitHub repo (is my local
    /// in sync with what's on GitHub), distinct from the build-output compare.</summary>
    [RelayCommand]
    private void CompareWithGitHub()
    {
        var s = _settings.LoadAppSettings(_entry.Name);
        if (string.IsNullOrWhiteSpace(s.GitHubRepo))
        {
            _dialog.Alert("GitHub Not Configured",
                "Set this app's GitHub repository in Settings → GitHub first.");
            return;
        }
        var token = string.IsNullOrWhiteSpace(s.GitHubToken) ? _settings.Global.GitHubToken : s.GitHubToken;

        // Local SOURCE folder: the repo clone, else the solution's directory, else the app folder.
        string localDir;
        if (!string.IsNullOrWhiteSpace(s.GitHubLocalPath))
            localDir = s.GitHubLocalPath!;
        else if (!string.IsNullOrWhiteSpace(_entry.SolutionPath)
                 && ProjectLocator.Resolve(_entry.SolutionPath) is { } target)
            localDir = System.IO.Path.GetDirectoryName(target) ?? _entry.FolderPath;
        else
            localDir = _entry.FolderPath;

        new GitHubCompareWindow(_github, _sourceCompare, s.GitHubRepo!, token, localDir, _entry.Name)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        }.ShowDialog();
    }

    // The source to scan: the app's configured solution/source path, else the local repo clone,
    // else the app's own folder.
    private string SourceScanPath()
    {
        if (!string.IsNullOrWhiteSpace(_entry.SolutionPath)) return _entry.SolutionPath;
        var s = _settings.LoadAppSettings(_entry.Name);
        return !string.IsNullOrWhiteSpace(s.GitHubLocalPath) ? s.GitHubLocalPath! : _entry.FolderPath;
    }

    /// <summary>Opens the dependency vulnerability scanner for this app's source (the local repo clone,
    /// or a project the user picks).</summary>
    [RelayCommand]
    private void ScanDependencies()
        => new VulnerabilityScanWindow(_vulnScan, _settings, SourceScanPath(), _entry.Name)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        }.ShowDialog();

    /// <summary>Opens the third-party license compliance scanner for this app's source.</summary>
    [RelayCommand]
    private void ScanLicenses()
        => new LicenseScanWindow(_licenseScan, _settings, SourceScanPath(), _entry.Name)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        }.ShowDialog();

    public void Initialize(AppEntry entry, MainViewModel main)
    {
        _entry = entry;
        _main = main;
        _channelFilter = _settings.Global.VersionChannelFilter ?? "All";
        OnPropertyChanged(nameof(ChannelFilter));
        OnPropertyChanged(nameof(IsChannelAll));
        OnPropertyChanged(nameof(IsChannelStable));
        OnPropertyChanged(nameof(IsChannelBeta));
        RebuildVersions();
        CheckChangelogEntries();
        RaiseWorkflowChanged();

        GitHubStatus = null;
        GitHubNewer = false;
        HasLastCommit = false;
        LastCommitSummary = string.Empty;
        LastCommitMeta = string.Empty;
        GitHubConfigured = !string.IsNullOrWhiteSpace(_settings.LoadAppSettings(entry.Name).GitHubRepo);
        OnPropertyChanged(nameof(HasSolution));
        OnPropertyChanged(nameof(HasScanSource));
        OnPropertyChanged(nameof(SolutionPathDisplay));
        BuildFromSolutionCommand.NotifyCanExecuteChanged();
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
        var slnFolder = !string.IsNullOrWhiteSpace(_entry.SolutionPath)
            && ProjectLocator.Resolve(_entry.SolutionPath) is { } t
            ? System.IO.Path.GetDirectoryName(t) : null;
        new ReleaseNotesWindow(_github, _changelog, s.GitHubRepo!, token,
            _entry.FolderPath, _entry.Name, _entry.LatestVersion?.VersionNumber, solutionFolder: slnFolder)
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

    private bool CanBuildFromSolution() => _session.CanScan && HasSolution && !IsBuilding;

    /// <summary>Builds the app's release straight from its solution/project (`dotnet publish -c Release`),
    /// then offers to scan the published output into a new version — same overlay as the GitHub build.</summary>
    [RelayCommand(CanExecute = nameof(CanBuildFromSolution))]
    private async Task BuildFromSolution()
    {
        var target = ProjectLocator.Resolve(_entry.SolutionPath);
        if (target is null)
        {
            _dialog.Alert("Solution Not Found",
                $"No .sln or .csproj found at the app's source path:\n{_entry.SolutionPath}");
            return;
        }

        var slnDir = System.IO.Path.GetDirectoryName(target) ?? _entry.SolutionPath;
        var outDir = System.IO.Path.Combine(slnDir, "ForgeTekPublish");
        _buildArtifactPath = outDir;

        BuildLog.Clear();
        ShowBuildOverlay = true;
        BuildFinished = false;
        BuildSucceeded = false;
        IsBuilding = true;
        _buildCts = new CancellationTokenSource();
        var progress = new Progress<string>(line => BuildLog.Add(line));
        try
        {
            try { if (System.IO.Directory.Exists(outDir)) System.IO.Directory.Delete(outDir, recursive: true); }
            catch (Exception ex) { BuildLog.Add($"(could not clean output: {ex.Message})"); }

            var args = $"publish \"{target}\" -c Release -o \"{outDir}\"";
            BuildLog.Add($"> dotnet {args}");
            await ProcessRunner.RunOrThrowAsync("dotnet", args, slnDir, progress, _buildCts.Token);
            BuildSucceeded = true;
            BuildLog.Add(string.Empty);
            BuildLog.Add($"✔ Build complete. Artifact: {outDir}");
        }
        catch (ToolNotFoundException)
        {
            BuildSucceeded = false;
            BuildLog.Add("✗ The .NET SDK (dotnet) was not found on PATH.");
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

    partial void OnSelectedVersionChanged(AppVersion? value)
    {
        RaiseWorkflowChanged();
        RefreshApprovalPanel();
    }

    partial void OnReviewNoteDraftChanged(string value)
    {
        AddReviewNoteCommand.NotifyCanExecuteChanged();
        RejectSelectedCommand.NotifyCanExecuteChanged();
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
        CollectBlobGarbage();   // the retracted version's now-unreferenced source blobs can be dropped

        var vIdx = Versions.IndexOf(v);
        if (vIdx >= 0) { Versions.RemoveAt(vIdx); Versions.Insert(vIdx, v); }

        _main.RefreshSidebar(_entry);
        SelectedVersion = null;
    }

    // Best-effort, off-thread sweep of source blobs no longer referenced by a live version (networked only).
    private void CollectBlobGarbage() => _ = Task.Run(async () =>
    {
        try { await _blobs.CollectGarbageAsync(); }
        catch (Exception ex) { _log.Write("Blobs", $"Source-file garbage collection failed: {ex.Message}"); }
    });

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
            // Off the UI thread (like the dashboard's check) so the transport can't deadlock the dispatcher.
            await Task.Run(() => _publish.RetractAsync(s, v, appKey, packageFileName, catalogFileName, rollbackToVersion, progress));
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
        CollectBlobGarbage();   // a scrapped version's source blobs are no longer referenced

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

        // Networked: capture the scanned files in the shared store so this version can be (re)packaged on
        // another machine that doesn't have this source folder. No-op standalone (NullFileBlobStore).
        try
        {
            await Task.Run(() => _blobs.StoreAsync(folderPath, files));
        }
        catch (Exception ex) { _log.Write("Scan", $"Storing source files in the database failed: {ex.Message}"); }

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
        var result = _dialog.ShowAddEditApp(_entry.Name, _entry.FolderPath, _entry.AccentColor, _entry.SolutionPath);
        if (result is null) return;
        _entry.Name = result.Name;
        _entry.FolderPath = result.Path;
        _entry.AccentColor = result.AccentColor;
        _entry.SolutionPath = result.SolutionPath;
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
        CollectBlobGarbage();   // the deleted app's source blobs are now unreferenced
        _main.NavigateToWelcome();
    }
}
