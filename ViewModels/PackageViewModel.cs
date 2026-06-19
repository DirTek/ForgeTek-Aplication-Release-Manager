using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Helpers;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;
using ForgeTekUpdatePackager.Services.Publishing;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class PackageViewModel : ObservableObject
{
    private MainViewModel _main = null!;
    private readonly IStorageService _storage;
    private readonly ISigningService _signing;
    private readonly IScannerService _scanner;
    private readonly IManifestService _manifest;
    private readonly IPackagingService _packaging;
    private readonly IPublishService _publish;
    private readonly IUpdateCatalogService _catalog;
    private readonly ILogService _log;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialog;
    private AppEntry _entry = null!;
    private AppVersion _version = null!;
    private readonly string? _signToolPath;
    private bool _isDiffVersion;
    private IReadOnlyList<FileRecord> _incrementalFiles = [];
    private IReadOnlyList<FileRecord> _fullFiles = [];
    private string _appKey = string.Empty;
    private string _catalogOutputPath = string.Empty;
    private AppSettings _appSettings = new();
    private CancellationTokenSource? _cts;

    public PackageViewModel(IStorageService storage, ISigningService signing, IScannerService scanner,
        ISettingsService settings, ILogService log, IPackagingService packaging, IPublishService publish,
        IManifestService manifest, IUpdateCatalogService catalog, IDialogService dialog)
    {
        _storage = storage;
        _signing = signing;
        _scanner = scanner;
        _settings = settings;
        _log = log;
        _packaging = packaging;
        _publish = publish;
        _manifest = manifest;
        _catalog = catalog;
        _dialog = dialog;
        _signToolPath = signing.FindSignTool();
    }

    public void Initialize(AppEntry entry, AppVersion version, MainViewModel main, PackageStep? startFrom = null)
    {
        _entry = entry;
        _version = version;
        _main = main;
        _appKey = entry.Name.ToLowerInvariant().Replace(" ", "");

        var idx = entry.Versions.IndexOf(version);
        var baseVersion = idx > 0 ? entry.Versions[idx - 1] : null;
        _isDiffVersion = baseVersion is not null;

        _fullFiles        = version.NonDebugFiles.ToList();
        _incrementalFiles = ComputeIncrementalFiles(version, baseVersion);

        if (!_isDiffVersion)
            SelectedPackageType = PackageType.Full;

        foreach (var f in _signing.GetSignableFiles(version.Files, entry.FolderPath, baseVersion))
        {
            var item = new SignableFileItem(f);
            item.PropertyChanged += (_, _) => SignCommand.NotifyCanExecuteChanged();
            SignableFiles.Add(item);
        }

        var appSettings = _settings.LoadAppSettings(entry.Name);
        _appSettings = appSettings;
        var versionDir  = _settings.GetVersionOutputPath(entry.Name, version.VersionNumber, appSettings);
        var ext         = string.IsNullOrWhiteSpace(appSettings.PackageExtension)
                              ? "ftu"
                              : appSettings.PackageExtension.TrimStart('.');

        ManifestOutputPath = Path.Combine(versionDir, "manifest.json");

        var nameVars = MacroEngine.StandardVars(
            entry.Name, version.VersionNumber, version.Channel.ToString(), _settings.Global.CompanyName);
        var packageBaseName = string.IsNullOrWhiteSpace(appSettings.PackageNameTemplate)
            ? $"{entry.Name}-{version.VersionNumber}"
            : MacroEngine.Resolve(appSettings.PackageNameTemplate, nameVars);
        packageBaseName = StorageService.Sanitize(packageBaseName);
        if (string.IsNullOrWhiteSpace(packageBaseName)) packageBaseName = $"{entry.Name}-{version.VersionNumber}";
        PackageOutputPath   = Path.Combine(versionDir, $"{packageBaseName}.{ext}");

        var releaseAppDir = Path.GetDirectoryName(versionDir) ?? string.Empty;
        _catalogOutputPath = Path.Combine(releaseAppDir, $"{_appKey}.json");

        var globalSettings = _settings.Global;
        if (globalSettings.UseStoreCert && !string.IsNullOrWhiteSpace(globalSettings.StoreCertThumbprint))
        {
            UseStoreCert    = true;
            StoreThumbprint = globalSettings.StoreCertThumbprint;
        }
        else if (globalSettings.UseGlobalCert && !string.IsNullOrWhiteSpace(globalSettings.GlobalCertPath))
        {
            UseStoreCert = false;
            PfxPath    = globalSettings.GlobalCertPath;
            PfxPassword = globalSettings.GlobalCertPassword ?? string.Empty;
        }
        else if (appSettings.UseStoreCert && !string.IsNullOrWhiteSpace(appSettings.StoreCertThumbprint))
        {
            UseStoreCert    = true;
            StoreThumbprint = appSettings.StoreCertThumbprint;
        }
        else
        {
            UseStoreCert = false;
            if (!string.IsNullOrWhiteSpace(appSettings.DefaultCertPath))
                PfxPath = appSettings.DefaultCertPath;
            if (!string.IsNullOrWhiteSpace(appSettings.DefaultCertPassword))
                PfxPassword = appSettings.DefaultCertPassword;
        }

        var savedStep = version.PipelineStep;
        if (startFrom is not null)
        {
            CurrentStep = startFrom.Value;
            if (startFrom.Value > PackageStep.Sign)     IsSigningComplete     = true;
            if (startFrom.Value > PackageStep.Manifest)  IsManifestComplete    = true;
            if (startFrom.Value > PackageStep.Package)   IsPackagingComplete   = true;
            if (startFrom.Value > PackageStep.Json)      IsJsonComplete        = true;
        }
        else if (savedStep is not null && savedStep.Value < PackageStep.Ftp)
        {
            CurrentStep = savedStep.Value + 1;
            if (savedStep.Value >= PackageStep.Sign)     IsSigningComplete     = true;
            if (savedStep.Value >= PackageStep.Manifest)  IsManifestComplete    = true;
            if (savedStep.Value >= PackageStep.Package)   IsPackagingComplete   = true;
            if (savedStep.Value >= PackageStep.Json)      IsJsonComplete        = true;
        }

        if (_signToolPath is null)
            Log.Add("⚠  signtool.exe not found — install the Windows 10/11 SDK or add it to PATH.");
    }

    private static IReadOnlyList<FileRecord> ComputeIncrementalFiles(AppVersion version, AppVersion? baseVersion)
    {
        var nonDebug = version.NonDebugFiles.ToList();
        if (baseVersion is null) return nonDebug;
        var baseMap = baseVersion.NonDebugFiles.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        return nonDebug.Where(f => !baseMap.TryGetValue(f.Path, out var bf) || bf.Checksum != f.Checksum).ToList();
    }

    // ── Publish target file names ─────────────────────────────────────────
    private string PackageFileName => Path.GetFileName(PackageOutputPath);
    private string CatalogFileName => $"{_appKey}.json";

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
    [NotifyPropertyChangedFor(nameof(SignState))]
    [NotifyPropertyChangedFor(nameof(ManifestState))]
    [NotifyPropertyChangedFor(nameof(PackageState))]
    [NotifyPropertyChangedFor(nameof(JsonState))]
    [NotifyPropertyChangedFor(nameof(FtpState))]
    [NotifyCanExecuteChangedFor(nameof(BuildPackageCommand))]
    private PackageStep _currentStep = PackageStep.Sign;

    // "Done" | "Current" | "Pending" for the pipeline stepper (drives each node's appearance).
    public string SignState     => StepStateOf(PackageStep.Sign);
    public string ManifestState => StepStateOf(PackageStep.Manifest);
    public string PackageState  => StepStateOf(PackageStep.Package);
    public string JsonState     => StepStateOf(PackageStep.Json);
    public string FtpState      => StepStateOf(PackageStep.Ftp);

    private string StepStateOf(PackageStep step)
        => CurrentStep > step ? "Done" : CurrentStep == step ? "Current" : "Pending";

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
        PackageStep.Ftp      => "Step 5 of 5 — Publish",
        _                    => string.Empty,
    };

    public string AdvanceLabel => CurrentStep == PackageStep.Ftp ? "Finish ✔" : "Next →";

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

    public bool   IsGlobalCertActive   => _settings.Global.UseGlobalCert
                                       && !string.IsNullOrWhiteSpace(_settings.Global.GlobalCertPath)
                                       && File.Exists(_settings.Global.GlobalCertPath);
    public bool   IsGlobalCertInactive => !IsGlobalCertActive;
    public string GlobalCertFileName   => IsGlobalCertActive
                                          ? Path.GetFileName(_settings.Global.GlobalCertPath!)
                                          : string.Empty;

    public string AppName      => _entry.Name;
    public string VersionLabel => $"v{_version.VersionNumber}  •  {_version.ScanDate:yyyy-MM-dd HH:mm}";

    public bool HasFtpSettings => _publish.IsConfigured(_appSettings);

    public string FtpSummary => HasFtpSettings
        ? $"Publishing via {_publish.ProviderName(_appSettings)}"
        : "No publish target configured — open App Settings to add one.";

    public ObservableCollection<string> Log { get; } = [];

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
            _version.PublishedDate = DateTime.Now;
            _version.PublishedBy = _main.Session.ActorName;
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
}
