using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class PackageTypeOption : ObservableObject
{
    public PackageType Value { get; }
    public string Label { get; }
    public string Description { get; }
    [ObservableProperty] private bool _isSelected;
    public PackageTypeOption(PackageType value, string label, string description, bool selected = false)
    {
        Value = value; Label = label; Description = description; _isSelected = selected;
    }
}

public partial class SignableFileItem : ObservableObject
{
    public string RelativePath { get; }
    public string FullPath { get; }
    public string BadgeText { get; }
    [ObservableProperty] private bool _isSelected = true;
    public SignableFileItem(SignableFile file)
    {
        RelativePath = file.RelativePath;
        FullPath     = file.FullPath;
        BadgeText    = file.Change switch
        {
            SignableFileChange.Added    => "+",
            SignableFileChange.Modified => "~",
            _                          => string.Empty,
        };
    }
}

public partial class PackageViewModel : ObservableObject
{
    private readonly AppEntry   _entry;
    private readonly AppVersion _version;
    private readonly MainViewModel  _main;
    private readonly SigningService  _signing;
    private readonly ScannerService  _scanner;
    private readonly StorageService  _storage;
    private readonly ManifestService _manifest = new();
    private readonly PackagingService _packaging = new();
    private readonly FtpService _ftpService = new();
    private readonly UpdateCatalogService _catalog = new();
    private readonly LogService _log;
    private readonly string? _signToolPath;
    private readonly bool _isDiffVersion;
    private readonly IReadOnlyList<FileRecord> _incrementalFiles;
    private readonly IReadOnlyList<FileRecord> _fullFiles;
    private readonly SettingsService _settings;
    private readonly string _appKey;
    private readonly string _catalogOutputPath;
    private readonly string? _ftpHost;
    private readonly int     _ftpPort;
    private readonly string  _ftpUsername;
    private readonly string  _ftpPassword;
    private readonly string? _ftpRemotePath;
    private readonly string? _baseDownloadUrl;
    private CancellationTokenSource? _cts;

    // ── Step state ─────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignCurrent))]
    [NotifyPropertyChangedFor(nameof(IsManifestCurrent))]
    [NotifyPropertyChangedFor(nameof(IsPackageCurrent))]
    [NotifyPropertyChangedFor(nameof(IsJsonCurrent))]
    [NotifyPropertyChangedFor(nameof(IsFtpCurrent))]
    [NotifyPropertyChangedFor(nameof(IsSignDone))]
    [NotifyPropertyChangedFor(nameof(IsManifestDone))]
    [NotifyPropertyChangedFor(nameof(IsPackageDone))]
    [NotifyPropertyChangedFor(nameof(IsJsonDone))]
    [NotifyPropertyChangedFor(nameof(IsSignStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsManifestStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsPackageStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsJsonStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsFtpStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsSignReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsManifestReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsPackageReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsJsonReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsFtpReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsSkipVisible))]
    [NotifyPropertyChangedFor(nameof(StepTitle))]
    [NotifyPropertyChangedFor(nameof(AdvanceLabel))]
    [NotifyCanExecuteChangedFor(nameof(BuildPackageCommand))]
    private PackageStep _currentStep = PackageStep.Sign;

    public bool IsSignCurrent     => CurrentStep == PackageStep.Sign;
    public bool IsManifestCurrent => CurrentStep == PackageStep.Manifest;
    public bool IsPackageCurrent  => CurrentStep == PackageStep.Package;
    public bool IsJsonCurrent     => CurrentStep == PackageStep.Json;
    public bool IsFtpCurrent      => CurrentStep == PackageStep.Ftp;

    public bool IsSignDone     => CurrentStep > PackageStep.Sign;
    public bool IsManifestDone => CurrentStep > PackageStep.Manifest;
    public bool IsPackageDone  => CurrentStep > PackageStep.Package;
    public bool IsJsonDone     => CurrentStep > PackageStep.Json;

    public string StepTitle => CurrentStep switch
    {
        PackageStep.Sign     => "Step 1 of 5 — Sign Files",
        PackageStep.Manifest => "Step 2 of 5 — Build Manifest",
        PackageStep.Package  => "Step 3 of 5 — Package",
        PackageStep.Json     => "Step 4 of 5 — Generate Update Catalog",
        PackageStep.Ftp      => "Step 5 of 5 — Upload to FTP",
        _                    => string.Empty,
    };

    public string AdvanceLabel => CurrentStep == PackageStep.Ftp ? "Finish ✔" : "Next →";

    // ── Operation state ────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignCommand))]
    [NotifyPropertyChangedFor(nameof(IsOperationInProgress))]
    [NotifyPropertyChangedFor(nameof(IsNotInProgress))]
    [NotifyPropertyChangedFor(nameof(IsSignStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsManifestStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsPackageStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsJsonStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsFtpStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsSignReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsManifestReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsPackageReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsJsonReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsFtpReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsSkipVisible))]
    private bool _isSigning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateManifestCommand))]
    [NotifyPropertyChangedFor(nameof(IsOperationInProgress))]
    [NotifyPropertyChangedFor(nameof(IsNotInProgress))]
    [NotifyPropertyChangedFor(nameof(IsSignStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsManifestStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsPackageStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsJsonStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsFtpStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsSignReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsManifestReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsPackageReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsJsonReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsFtpReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsSkipVisible))]
    private bool _isGenerating;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildPackageCommand))]
    [NotifyPropertyChangedFor(nameof(IsOperationInProgress))]
    [NotifyPropertyChangedFor(nameof(IsNotInProgress))]
    [NotifyPropertyChangedFor(nameof(IsSignStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsManifestStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsPackageStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsJsonStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsFtpStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsSignReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsManifestReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsPackageReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsJsonReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsFtpReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsSkipVisible))]
    private bool _isPackaging;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateUpdateJsonCommand))]
    [NotifyPropertyChangedFor(nameof(IsOperationInProgress))]
    [NotifyPropertyChangedFor(nameof(IsNotInProgress))]
    [NotifyPropertyChangedFor(nameof(IsJsonStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsFtpStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsJsonReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsSkipVisible))]
    private bool _isGeneratingJson;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadToFtpCommand))]
    [NotifyPropertyChangedFor(nameof(IsOperationInProgress))]
    [NotifyPropertyChangedFor(nameof(IsNotInProgress))]
    [NotifyPropertyChangedFor(nameof(IsFtpStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsFtpReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsSkipVisible))]
    private bool _isUploading;

    public bool IsOperationInProgress => IsSigning || IsGenerating || IsPackaging || IsGeneratingJson || IsUploading;
    public bool IsNotInProgress       => !IsOperationInProgress;

    public bool IsSignStepIdle     => IsSignCurrent     && IsNotInProgress && !IsSigningComplete;
    public bool IsManifestStepIdle => IsManifestCurrent && IsNotInProgress && !IsManifestComplete;
    public bool IsPackageStepIdle  => IsPackageCurrent  && IsNotInProgress && !IsPackagingComplete;
    public bool IsJsonStepIdle     => IsJsonCurrent     && IsNotInProgress && !IsJsonComplete;
    public bool IsFtpStepIdle      => IsFtpCurrent      && IsNotInProgress && !IsFtpComplete && HasFtpSettings;

    public bool IsSignReadyToAdvance     => IsSignCurrent     && IsSigningComplete   && IsNotInProgress;
    public bool IsManifestReadyToAdvance => IsManifestCurrent && IsManifestComplete  && IsNotInProgress;
    public bool IsPackageReadyToAdvance  => IsPackageCurrent  && IsPackagingComplete && IsNotInProgress;
    public bool IsJsonReadyToAdvance     => IsJsonCurrent     && IsJsonComplete      && IsNotInProgress;
    public bool IsFtpReadyToAdvance      => IsFtpCurrent      && IsFtpComplete       && IsNotInProgress;

    public bool IsReadyToAdvance => IsSignReadyToAdvance || IsManifestReadyToAdvance
                                 || IsPackageReadyToAdvance || IsJsonReadyToAdvance || IsFtpReadyToAdvance;

    public bool IsSkipVisible => (IsSignCurrent && IsNotInProgress && !IsSigningComplete)
                              || (IsFtpCurrent  && IsNotInProgress && !IsFtpComplete);

    // ── Sign step ──────────────────────────────────────────────────────────

    public bool   IsGlobalCertActive   => _settings.Global.UseGlobalCert
                                       && !string.IsNullOrWhiteSpace(_settings.Global.GlobalCertPath)
                                       && File.Exists(_settings.Global.GlobalCertPath);
    public bool   IsGlobalCertInactive => !IsGlobalCertActive;
    public string GlobalCertFileName   => IsGlobalCertActive
                                          ? Path.GetFileName(_settings.Global.GlobalCertPath!)
                                          : string.Empty;

    public string AppName      => _entry.Name;
    public string VersionLabel => $"v{_version.VersionNumber}  •  {_version.ScanDate:yyyy-MM-dd HH:mm}";

    public string FilesLabel
    {
        get
        {
            var count = SignableFiles.Count;
            if (count == 0) return "No signable files found (.exe, .dll, .sys, .ocx, .msi, .cab, .cat)";
            var scope = _isDiffVersion ? " — new and modified only" : string.Empty;
            return count == 1 ? $"1 file{scope}" : $"{count} files{scope}";
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignCommand))]
    private string _pfxPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsSignReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsSkipVisible))]
    private bool _isSigningComplete;

    public string PfxPassword { get; set; } = string.Empty;
    public ObservableCollection<SignableFileItem> SignableFiles { get; } = [];

    // ── Manifest step ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateManifestCommand))]
    private string _manifestOutputPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManifestStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsManifestReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsReadyToAdvance))]
    private bool _isManifestComplete;

    // ── Package step ───────────────────────────────────────────────────────

    public bool HasManifest => _version.HasManifest;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildPackageCommand))]
    private string _packageOutputPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PackageFilesLabel))]
    [NotifyPropertyChangedFor(nameof(IsIncrementalSelected))]
    [NotifyPropertyChangedFor(nameof(IsFullSelected))]
    [NotifyCanExecuteChangedFor(nameof(BuildPackageCommand))]
    [NotifyCanExecuteChangedFor(nameof(UploadToFtpCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateUpdateJsonCommand))]
    private bool _isPackagingComplete;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PackageFilesLabel))]
    [NotifyPropertyChangedFor(nameof(IsIncrementalSelected))]
    [NotifyPropertyChangedFor(nameof(IsFullSelected))]
    private PackageType _selectedPackageType = PackageType.Incremental;

    public bool IsIncrementalSelected
    {
        get => SelectedPackageType == PackageType.Incremental;
        set { if (value) SelectedPackageType = PackageType.Incremental; }
    }

    public bool IsFullSelected
    {
        get => SelectedPackageType == PackageType.Full;
        set { if (value) SelectedPackageType = PackageType.Full; }
    }

    public bool IsIncrementalAvailable => _isDiffVersion;

    public string PackageFilesLabel
    {
        get
        {
            var files = SelectedPackageType == PackageType.Incremental ? _incrementalFiles : _fullFiles;
            return SelectedPackageType == PackageType.Incremental
                ? $"{files.Count} file(s) — added and modified only"
                : $"{files.Count} file(s) — all non-debug files";
        }
    }

    // ── JSON step ──────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJsonStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsJsonReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsReadyToAdvance))]
    [NotifyCanExecuteChangedFor(nameof(UploadToFtpCommand))]
    private bool _isJsonComplete;

    public string CatalogLocalPath  => _catalogOutputPath;
    public string CatalogRemotePath => BuildRemotePath(_ftpRemotePath, _appKey, null, $"{_appKey}.json");
    public string PackageRemotePath => BuildRemotePath(_ftpRemotePath, _appKey, _version.VersionNumber, Path.GetFileName(PackageOutputPath));
    public string PackageDownloadUrl => BuildDownloadUrl(_baseDownloadUrl, _appKey, _version.VersionNumber, Path.GetFileName(PackageOutputPath));

    public bool HasFtpSettings => !string.IsNullOrWhiteSpace(_ftpHost);

    public string FtpSummary => HasFtpSettings
        ? $"{_ftpHost}:{_ftpPort}"
        : "No FTP credentials configured — open App Settings to add them.";

    // ── FTP step ───────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFtpStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsFtpReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsReadyToAdvance))]
    private bool _isFtpComplete;

    // ── Shared log ─────────────────────────────────────────────────────────

    public ObservableCollection<string> Log { get; } = [];

    // ── Constructor ────────────────────────────────────────────────────────

    public PackageViewModel(AppEntry entry, AppVersion version, MainViewModel main, StorageService storage, SigningService signing, ScannerService scanner, SettingsService settings, LogService log, PackageStep? startFrom = null)
    {
        _entry    = entry;
        _version  = version;
        _main     = main;
        _storage  = storage;
        _signing  = signing;
        _scanner  = scanner;
        _settings = settings;
        _log      = log;
        _signToolPath = signing.FindSignTool();
        _appKey = entry.Name.ToLowerInvariant().Replace(" ", "");

        var idx = entry.Versions.IndexOf(version);
        var baseVersion = idx > 0 ? entry.Versions[idx - 1] : null;
        _isDiffVersion = baseVersion is not null;

        _fullFiles        = version.NonDebugFiles.ToList();
        _incrementalFiles = ComputeIncrementalFiles(version, baseVersion);

        if (!_isDiffVersion)
            _selectedPackageType = PackageType.Full;

        foreach (var f in signing.GetSignableFiles(version.Files, entry.FolderPath, baseVersion))
        {
            var item = new SignableFileItem(f);
            item.PropertyChanged += (_, _) => SignCommand.NotifyCanExecuteChanged();
            SignableFiles.Add(item);
        }

        var appSettings = settings.LoadAppSettings(entry.Name);
        var versionDir  = settings.GetVersionOutputPath(entry.Name, version.VersionNumber, appSettings);
        var ext         = string.IsNullOrWhiteSpace(appSettings.PackageExtension)
                              ? "ftu"
                              : appSettings.PackageExtension.TrimStart('.');

        _manifestOutputPath = Path.Combine(versionDir, "manifest.json");
        PackageOutputPath   = Path.Combine(versionDir, $"{entry.Name}-{version.VersionNumber}.{ext}");

        // Catalog lives one level up from the version folder
        var releaseAppDir = Path.GetDirectoryName(versionDir)!;
        _catalogOutputPath = Path.Combine(releaseAppDir, $"{_appKey}.json");

        var globalSettings = settings.Global;
        if (globalSettings.UseGlobalCert && !string.IsNullOrWhiteSpace(globalSettings.GlobalCertPath))
        {
            _pfxPath    = globalSettings.GlobalCertPath;
            PfxPassword = globalSettings.GlobalCertPassword ?? string.Empty;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(appSettings.DefaultCertPath))
                _pfxPath = appSettings.DefaultCertPath;
            if (!string.IsNullOrWhiteSpace(appSettings.DefaultCertPassword))
                PfxPassword = appSettings.DefaultCertPassword;
        }

        _ftpHost         = appSettings.FtpHost;
        _ftpPort         = appSettings.FtpPort == 0 ? 21 : appSettings.FtpPort;
        _ftpUsername     = appSettings.FtpUsername ?? string.Empty;
        _ftpPassword     = appSettings.FtpPassword ?? string.Empty;
        _ftpRemotePath   = appSettings.FtpRemotePath;
        _baseDownloadUrl = appSettings.BaseDownloadUrl;

        // Restore or override the starting step
        var savedStep = version.PipelineStep;
        if (startFrom is not null)
        {
            _currentStep = startFrom.Value;
            // Mark all prior steps as done
            if (startFrom.Value > PackageStep.Sign)     IsSigningComplete     = true;
            if (startFrom.Value > PackageStep.Manifest)  IsManifestComplete    = true;
            if (startFrom.Value > PackageStep.Package)   IsPackagingComplete   = true;
            if (startFrom.Value > PackageStep.Json)      IsJsonComplete        = true;
        }
        else if (savedStep is not null && savedStep.Value < PackageStep.Ftp)
        {
            // Resume from the step after the last completed one
            _currentStep = savedStep.Value + 1;
            if (savedStep.Value >= PackageStep.Sign)     IsSigningComplete     = true;
            if (savedStep.Value >= PackageStep.Manifest)  IsManifestComplete    = true;
            if (savedStep.Value >= PackageStep.Package)   IsPackagingComplete   = true;
            if (savedStep.Value >= PackageStep.Json)      IsJsonComplete        = true;
        }

        if (_signToolPath is null)
            Log.Add("⚠  signtool.exe not found — install the Windows 10/11 SDK or add it to PATH.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static IReadOnlyList<FileRecord> ComputeIncrementalFiles(AppVersion version, AppVersion? baseVersion)
    {
        var nonDebug = version.NonDebugFiles.ToList();
        if (baseVersion is null) return nonDebug;
        var baseMap = baseVersion.NonDebugFiles.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        return nonDebug.Where(f => !baseMap.TryGetValue(f.Path, out var bf) || bf.Checksum != f.Checksum).ToList();
    }

    private static string BuildRemotePath(string? basePath, string appKey, string? version, string filename)
    {
        // Strip any accidental ftp:// URL prefix — only the server-side path is needed
        var serverPath = basePath ?? string.Empty;
        if (serverPath.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
            serverPath.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase))
        {
            var afterScheme = serverPath[(serverPath.IndexOf("//") + 2)..];
            // Drop the host part (everything up to the first /)
            var slashIdx = afterScheme.IndexOf('/');
            serverPath = slashIdx >= 0 ? afterScheme[slashIdx..] : "/";
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(serverPath)) parts.Add(serverPath.TrimEnd('/'));
        parts.Add(appKey);
        if (version is not null) parts.Add(version);
        parts.Add(filename);
        return "/" + string.Join("/", parts).TrimStart('/');
    }

    private static string BuildDownloadUrl(string? baseUrl, string appKey, string version, string filename)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return $"{appKey}/{version}/{filename}";

        // Ensure it uses http(s) — warn users who may have entered an ftp:// URL here
        var url = baseUrl.Trim().TrimEnd('/');
        if (url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url[(url.IndexOf("//") + 2)..];

        return $"{url}/{appKey}/{version}/{filename}";
    }

    // ── Pipeline navigation ────────────────────────────────────────────────

    [RelayCommand]
    private void Skip()
    {
        if (CurrentStep == PackageStep.Sign && !IsSigningComplete)
        {
            Log.Add(string.Empty);
            Log.Add("─── Skipped: Sign ───");
            CurrentStep = PackageStep.Manifest;
        }
        else if (CurrentStep == PackageStep.Ftp && !IsFtpComplete)
        {
            Log.Add(string.Empty);
            Log.Add("─── Skipped: FTP Upload ───");
            _storage.Update(_entry);
            _main.NavigateToDetail(_entry);
        }
    }

    [RelayCommand]
    private void Advance()
    {
        var next = CurrentStep + 1;
        if (next > PackageStep.Ftp)
        {
            _version.Status = VersionStatus.Published;
            _storage.Update(_entry);
            _main.NavigateToDetail(_entry);
            return;
        }
        CurrentStep = next;
    }

    [RelayCommand]
    private void CancelPipeline()
    {
        _cts?.Cancel();
        _main.NavigateToDetail(_entry);
    }

    [RelayCommand]
    private void StopOperation() => _cts?.Cancel();

    // ── Sign step commands ─────────────────────────────────────────────────

    [RelayCommand]
    private void SelectAll()   { foreach (var f in SignableFiles) f.IsSelected = true; }
    [RelayCommand]
    private void DeselectAll() { foreach (var f in SignableFiles) f.IsSelected = false; }

    [RelayCommand]
    private void BrowsePfx()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select PFX Certificate",
            Filter = "PFX Certificate (*.pfx)|*.pfx|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true) PfxPath = dlg.FileName;
    }

    [RelayCommand(CanExecute = nameof(CanSign))]
    private async Task SignAsync()
    {
        IsSigning = true;
        IsSigningComplete = false;
        Log.Clear();

        var selected = SignableFiles.Where(f => f.IsSelected).Select(f => f.FullPath).ToList();
        Log.Add($"Signing {selected.Count} of {SignableFiles.Count} file(s) with {Path.GetFileName(PfxPath)}…");

        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => Log.Add(msg));
        try
        {
            await _signing.SignFilesAsync(_signToolPath!, selected, PfxPath, PfxPassword, progress, _cts.Token);

            // Recompute checksums so the manifest captures the signed file hashes
            RecomputeSignedChecksums(selected);

            _version.Status = VersionStatus.Signed;
            _version.PipelineStep = PackageStep.Sign;
            _storage.Update(_entry);
            Log.Add(string.Empty);
            Log.Add("✔  Signing complete.");
            IsSigningComplete = true;
            _main.RefreshSidebar(_entry);
        }
        catch (OperationCanceledException) { Log.Add("Signing stopped."); }
        catch (Exception ex)              { Log.Add($"✗ {ex.Message}"); }
        finally { _cts.Dispose(); _cts = null; IsSigning = false; }
    }

    private void RecomputeSignedChecksums(IReadOnlyList<string> signedPaths)
    {
        foreach (var path in signedPaths)
        {
            var relPath = Path.GetRelativePath(_entry.FolderPath, path);
            var newChecksum = ScannerService.ComputeChecksum(path);

            // Update in _version.Files
            var record = _version.Files.FirstOrDefault(f => f.Path == relPath);
            if (record is not null)
                record.Checksum = newChecksum;

            // Update in _fullFiles
            var fullFile = _fullFiles.FirstOrDefault(f => f.Path == relPath);
            if (fullFile is not null)
                fullFile.Checksum = newChecksum;

            // Update in _incrementalFiles
            var incFile = _incrementalFiles.FirstOrDefault(f => f.Path == relPath);
            if (incFile is not null)
                incFile.Checksum = newChecksum;
        }
    }

    private bool CanSign()
        => !IsSigning && !IsGenerating && !IsPackaging
           && SignableFiles.Any(f => f.IsSelected)
           && !string.IsNullOrWhiteSpace(PfxPath)
           && _signToolPath is not null;

    // ── Manifest step commands ─────────────────────────────────────────────

    [RelayCommand]
    private void BrowseManifestOutput()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Manifest As",
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
            FileName = "manifest.json",
        };
        if (dlg.ShowDialog() == true) ManifestOutputPath = dlg.FileName;
    }

    [RelayCommand(CanExecute = nameof(CanGenerateManifest))]
    private async Task GenerateManifestAsync()
    {
        IsGenerating = true;
        IsManifestComplete = false;
        Log.Add(string.Empty);
        Log.Add($"Building manifest for v{_version.VersionNumber}…");

        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => Log.Add(msg));
        try
        {
            var json = await _manifest.GenerateAsync(_entry, _version, _incrementalFiles, _version.RemovedFiles, progress, _cts.Token);
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestOutputPath)!);
            await File.WriteAllTextAsync(ManifestOutputPath, json, _cts.Token);
            Log.Add(string.Empty);
            Log.Add($"✔  Manifest saved → {ManifestOutputPath}");
            _version.HasManifest = true;
            _version.PipelineStep = PackageStep.Manifest;
            _storage.Update(_entry);
            OnPropertyChanged(nameof(HasManifest));
            BuildPackageCommand.NotifyCanExecuteChanged();
            IsManifestComplete = true;
            _main.RefreshSidebar(_entry);
        }
        catch (OperationCanceledException) { Log.Add("Manifest generation stopped."); }
        catch (Exception ex)              { Log.Add($"✗ {ex.Message}"); }
        finally { _cts.Dispose(); _cts = null; IsGenerating = false; }
    }

    private bool CanGenerateManifest()
        => !IsSigning && !IsGenerating && !IsPackaging
           && !string.IsNullOrWhiteSpace(ManifestOutputPath);

    // ── Package step commands ──────────────────────────────────────────────

    [RelayCommand]
    private void BrowsePackageOutput()
    {
        var appSettings = _settings.LoadAppSettings(_entry.Name);
        var ext = string.IsNullOrWhiteSpace(appSettings.PackageExtension)
                      ? "ftu"
                      : appSettings.PackageExtension.TrimStart('.');
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Package As",
            Filter = $"ForgeTek Package (*.{ext})|*.{ext}|All files (*.*)|*.*",
            FileName = Path.GetFileName(PackageOutputPath),
            InitialDirectory = Path.GetDirectoryName(PackageOutputPath) ?? string.Empty,
        };
        if (dlg.ShowDialog() == true) PackageOutputPath = dlg.FileName;
    }

    [RelayCommand(CanExecute = nameof(CanBuildPackage))]
    private async Task BuildPackageAsync()
    {
        IsPackaging = true;
        IsPackagingComplete = false;
        Log.Add(string.Empty);

        var files = SelectedPackageType == PackageType.Incremental ? _incrementalFiles : _fullFiles;

        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => Log.Add(msg));
        try
        {
            var removedFiles = SelectedPackageType == PackageType.Incremental ? _version.RemovedFiles : null;
            var sha256 = await _packaging.BuildAsync(
                _entry, _version, files, SelectedPackageType,
                PackageOutputPath, ManifestOutputPath, removedFiles, progress, _cts.Token);

            Log.Add(string.Empty);
            Log.Add($"Package SHA-256: {sha256}");

            // Verify the written file is readable and internally consistent
            Log.Add(string.Empty);
            await _packaging.VerifyAsync(PackageOutputPath, progress, _cts.Token);

            _version.HasPackage       = true;
            _version.PackagePath      = PackageOutputPath;
            _version.PackageChecksum  = sha256;
            _version.PackageType      = SelectedPackageType;
            _version.Status      = VersionStatus.Packed;
            _version.PipelineStep = PackageStep.Package;
            _storage.Update(_entry);
            IsPackagingComplete = true;
            _main.RefreshSidebar(_entry);
        }
        catch (OperationCanceledException) { Log.Add("Packaging stopped."); }
        catch (Exception ex)              { Log.Add($"✗ {ex.Message}"); }
        finally { _cts.Dispose(); _cts = null; IsPackaging = false; }
    }

    private bool CanBuildPackage()
        => !IsSigning && !IsGenerating && !IsPackaging
           && !string.IsNullOrWhiteSpace(PackageOutputPath)
           && _version.HasManifest;

    // ── JSON step commands ─────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanGenerateJson))]
    private async Task GenerateUpdateJsonAsync()
    {
        IsGeneratingJson = true;
        IsJsonComplete = false;
        Log.Add(string.Empty);
        Log.Add("Building update catalog…");

        _cts = new CancellationTokenSource();
        try
        {
            string? existingJson = null;

            if (File.Exists(_catalogOutputPath))
            {
                existingJson = await File.ReadAllTextAsync(_catalogOutputPath, _cts.Token);
                Log.Add($"Loaded existing local catalog.");
            }
            else if (HasFtpSettings)
            {
                Log.Add("Checking FTP for existing catalog…");
                using var ftpCheckCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                ftpCheckCts.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    // Run on a thread-pool thread — FTP connect/download blocks the
                    // UI dispatcher if awaited directly on the UI thread.
                    existingJson = await Task.Run(() => _ftpService.TryDownloadStringAsync(
                        CatalogRemotePath, _ftpHost!, _ftpPort, _ftpUsername, _ftpPassword, ftpCheckCts.Token));
                    Log.Add(existingJson is not null
                        ? "Downloaded existing catalog from FTP."
                        : "No existing catalog on FTP — creating new.");
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                {
                    Log.Add("FTP check timed out — creating new catalog.");
                }
            }
            else
            {
                Log.Add("No existing catalog — creating new.");
            }

            var catalogJson = _catalog.BuildOrMerge(_appKey, _version, PackageDownloadUrl, existingJson);

            Directory.CreateDirectory(Path.GetDirectoryName(_catalogOutputPath)!);
            await File.WriteAllTextAsync(_catalogOutputPath, catalogJson, _cts.Token);

            Log.Add($"✔  Catalog saved → {_catalogOutputPath}");
            Log.Add(string.Empty);
            Log.Add(catalogJson.Length > 600 ? catalogJson[..600] + "\n…" : catalogJson);
            _version.PipelineStep = PackageStep.Json;
            _storage.Update(_entry);
            IsJsonComplete = true;
            _main.RefreshSidebar(_entry);
        }
        catch (OperationCanceledException) { Log.Add("Catalog generation stopped."); }
        catch (Exception ex)              { Log.Add($"✗  {ex.Message}"); }
        finally { _cts.Dispose(); _cts = null; IsGeneratingJson = false; }
    }

    private bool CanGenerateJson() => !IsGeneratingJson && _version.HasPackage;

    // ── FTP step commands ──────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanUploadToFtp))]
    private async Task UploadToFtpAsync()
    {
        IsUploading = true;
        IsFtpComplete = false;
        Log.Add(string.Empty);
        Log.Add($"Uploading to {_ftpHost} …");
        _log.Write("FTP", $"=== Upload session start — {_ftpHost}:{_ftpPort} ===");

        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => { Log.Add(msg); _log.Write("FTP", msg); });
        try
        {
            var uploads = new (string Local, string Remote)[]
            {
                (_version.PackagePath!, PackageRemotePath),
                (_catalogOutputPath,    CatalogRemotePath),
            };

            // Run entirely on a thread-pool thread so the UI stays responsive during
            // the upload and especially during the post-transfer ACK wait, which can
            // block for 10–30 s on shared-hosting servers.
            await Task.Run(() => _ftpService.UploadFilesAsync(
                uploads.Select(u => (u.Local, u.Remote)),
                _ftpHost!, _ftpPort, _ftpUsername, _ftpPassword, progress, _cts.Token));

            Log.Add(string.Empty);
            Log.Add("✔  All files uploaded.");

            // Persist remote paths and credentials so the retract operation can
            // delete the correct files from FTP later.
            _version.FtpPackageRemotePath = PackageRemotePath;
            _version.FtpCatalogRemotePath = CatalogRemotePath;
            _version.FtpHost              = _ftpHost;
            _version.FtpPort              = _ftpPort;
            _version.FtpUsername          = _ftpUsername;
            _version.FtpPassword          = _ftpPassword;
            _version.PipelineStep         = PackageStep.Ftp;
            _storage.Update(_entry);

            IsFtpComplete = true;
            _main.RefreshSidebar(_entry);
        }
        catch (OperationCanceledException) { Log.Add("Upload stopped."); }
        catch (Exception ex)              { Log.Add($"✗  {ex.Message}"); }
        finally { _cts.Dispose(); _cts = null; IsUploading = false; }
    }

    private bool CanUploadToFtp()
        => !IsUploading && HasFtpSettings
           && _version.HasPackage
           && IsJsonComplete
           && File.Exists(_catalogOutputPath);
}
