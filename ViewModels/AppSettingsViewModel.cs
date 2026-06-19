using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Dialogs;
using ForgeTekUpdatePackager.Helpers;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;
using ForgeTekUpdatePackager.Services.Publishing;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class AppSettingsViewModel : ObservableObject
{
    private MainViewModel _main = null!;
    private AppEntry _entry = null!;
    private readonly ISettingsService _settings;
    private readonly IFtpService _ftp;
    private readonly IPublishService _publish;

    public string AppName => _entry.Name;
    public string DefaultOutputBase => _settings.GetDefaultOutputBase(_entry.Name);

    public bool   GlobalCertOverrideActive   => _settings.Global.UseGlobalCert
                                             && !string.IsNullOrWhiteSpace(_settings.Global.GlobalCertPath)
                                             && File.Exists(_settings.Global.GlobalCertPath);
    public bool   GlobalCertOverrideInactive => !GlobalCertOverrideActive;
    public string GlobalCertOverrideName     => GlobalCertOverrideActive
                                                ? Path.GetFileName(_settings.Global.GlobalCertPath!)
                                                : string.Empty;

    [ObservableProperty] private string _outputFolder = string.Empty;
    [ObservableProperty] private string _defaultCertPath = string.Empty;

    public string DefaultCertPassword { get; set; } = string.Empty;

    [ObservableProperty] private bool _useStoreCert;

    [ObservableProperty] private string? _selectedStoreThumbprint;

    public ObservableCollection<StoreCertInfo> StoreCertificates { get; } = [];

    public bool HasStoreCert => !string.IsNullOrWhiteSpace(SelectedStoreThumbprint);

    [ObservableProperty] private string _packageExtension = string.Empty;
    [ObservableProperty] private string _packageNameTemplate = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private string _ftpHost = string.Empty;

    [ObservableProperty] private int _ftpPort = 21;
    [ObservableProperty] private string _ftpUsername = string.Empty;

    public string FtpPassword { get; set; } = string.Empty;

    [ObservableProperty] private string _ftpRemotePath = string.Empty;
    [ObservableProperty] private string _baseDownloadUrl = string.Empty;

    // ── Publish provider selection ────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFtp))]
    [NotifyPropertyChangedFor(nameof(IsSftp))]
    [NotifyPropertyChangedFor(nameof(IsS3))]
    [NotifyPropertyChangedFor(nameof(IsGitHubReleases))]
    private string _publishProvider = "Ftp";

    public bool IsFtp            => PublishProvider is null or "Ftp";
    public bool IsSftp           => PublishProvider == "Sftp";
    public bool IsS3             => PublishProvider == "S3";
    public bool IsGitHubReleases => PublishProvider == "GitHubReleases";

    // ── SFTP ──────────────────────────────────────────────────────────────
    [ObservableProperty] private string _sftpHost = string.Empty;
    [ObservableProperty] private int _sftpPort = 22;
    [ObservableProperty] private string _sftpUsername = string.Empty;
    public string SftpPassword { get; set; } = string.Empty;
    [ObservableProperty] private string _sftpRemotePath = string.Empty;
    [ObservableProperty] private string _sftpBaseDownloadUrl = string.Empty;

    // ── S3-compatible ─────────────────────────────────────────────────────
    [ObservableProperty] private string _s3Endpoint = string.Empty;
    [ObservableProperty] private string _s3Region = string.Empty;
    [ObservableProperty] private string _s3Bucket = string.Empty;
    [ObservableProperty] private string _s3AccessKey = string.Empty;
    public string S3SecretKey { get; set; } = string.Empty;
    [ObservableProperty] private string _s3Prefix = string.Empty;
    [ObservableProperty] private string _s3PublicBaseUrl = string.Empty;

    // ── GitHub Releases ───────────────────────────────────────────────────
    [ObservableProperty] private string _gitHubReleaseTag = "v{version}";
    [ObservableProperty] private string _gitHubCatalogTag = "updates";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool _isTestingConnection;

    [ObservableProperty] private string _connectionTestResult = string.Empty;

    // ── GitHub ────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestGitHubCommand))]
    private string _gitHubRepo = string.Empty;

    public string GitHubToken { get; set; } = string.Empty;

    [ObservableProperty] private string _gitHubLocalPath = string.Empty;
    [ObservableProperty] private string _gitHubBuildCommand = string.Empty;
    [ObservableProperty] private string _gitHubArtifactPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestGitHubCommand))]
    private bool _isTestingGitHub;

    [ObservableProperty] private string _gitHubTestResult = string.Empty;

    private readonly IGitHubService _github;

    private readonly ISettingsTemplateService _templates;
    private readonly IConnectionStatusCache _connCache;

    public AppSettingsViewModel(ISettingsService settings, IFtpService ftp, IGitHubService github,
        IPublishService publish, ISettingsTemplateService templates, IConnectionStatusCache connCache)
    {
        _settings = settings;
        _ftp = ftp;
        _github = github;
        _publish = publish;
        _templates = templates;
        _connCache = connCache;
    }

    // ── Pipeline templates ────────────────────────────────────────────────
    public ObservableCollection<SettingsTemplate> Templates { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyTemplateCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteTemplateCommand))]
    private SettingsTemplate? _selectedTemplate;

    [ObservableProperty] private string _templateStatus = string.Empty;

    private void RefreshTemplates(string? selectId = null)
    {
        Templates.Clear();
        foreach (var t in _templates.GetAll()) Templates.Add(t);
        if (selectId is not null)
            SelectedTemplate = Templates.FirstOrDefault(t => t.Id == selectId);
    }

    [RelayCommand(CanExecute = nameof(CanApplyTemplate))]
    private void ApplyTemplate()
    {
        var t = SelectedTemplate;
        if (t is null) return;

        // Apply only the fields the template specifies; leave the rest (and all secrets) untouched.
        if (!string.IsNullOrWhiteSpace(t.PublishProvider)) PublishProvider = t.PublishProvider!;
        if (t.PackageExtension is not null)    PackageExtension    = t.PackageExtension;
        if (t.PackageNameTemplate is not null) PackageNameTemplate = t.PackageNameTemplate;
        if (t.GitHubBuildCommand is not null)  GitHubBuildCommand  = t.GitHubBuildCommand;
        if (t.GitHubArtifactPath is not null)  GitHubArtifactPath  = t.GitHubArtifactPath;
        if (t.FtpRemotePath is not null)       FtpRemotePath       = t.FtpRemotePath;
        if (t.BaseDownloadUrl is not null)     BaseDownloadUrl     = t.BaseDownloadUrl;
        if (t.SftpRemotePath is not null)      SftpRemotePath      = t.SftpRemotePath;
        if (t.SftpBaseDownloadUrl is not null) SftpBaseDownloadUrl = t.SftpBaseDownloadUrl;
        if (t.S3Endpoint is not null)          S3Endpoint          = t.S3Endpoint;
        if (t.S3Region is not null)            S3Region            = t.S3Region;
        if (t.S3Bucket is not null)            S3Bucket            = t.S3Bucket;
        if (t.S3Prefix is not null)            S3Prefix            = t.S3Prefix;
        if (t.S3PublicBaseUrl is not null)     S3PublicBaseUrl     = t.S3PublicBaseUrl;
        if (t.GitHubReleaseTag is not null)    GitHubReleaseTag    = t.GitHubReleaseTag;
        if (t.GitHubCatalogTag is not null)    GitHubCatalogTag    = t.GitHubCatalogTag;

        TemplateStatus = $"Applied “{t.Name}”. Review the fields and click Save to keep them.";
    }

    private bool CanApplyTemplate() => SelectedTemplate is not null;

    [RelayCommand]
    private void SaveAsTemplate()
    {
        var dlg = new InputDialog("Save as Template",
            "Name this template (secrets like passwords and tokens are not saved):",
            string.IsNullOrWhiteSpace(SelectedTemplate?.Name) || (SelectedTemplate?.IsBuiltIn ?? true)
                ? string.Empty : SelectedTemplate!.Name)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (dlg.ShowDialog() != true) return;

        var template = new SettingsTemplate
        {
            Name = dlg.Value,
            PublishProvider     = string.IsNullOrWhiteSpace(PublishProvider) ? null : PublishProvider,
            PackageExtension    = NullIfEmpty(PackageExtension),
            PackageNameTemplate = NullIfEmpty(PackageNameTemplate),
            GitHubBuildCommand  = NullIfEmpty(GitHubBuildCommand),
            GitHubArtifactPath  = NullIfEmpty(GitHubArtifactPath),
            FtpRemotePath       = NullIfEmpty(FtpRemotePath),
            BaseDownloadUrl     = NullIfEmpty(BaseDownloadUrl),
            SftpRemotePath      = NullIfEmpty(SftpRemotePath),
            SftpBaseDownloadUrl = NullIfEmpty(SftpBaseDownloadUrl),
            S3Endpoint          = NullIfEmpty(S3Endpoint),
            S3Region            = NullIfEmpty(S3Region),
            S3Bucket            = NullIfEmpty(S3Bucket),
            S3Prefix            = NullIfEmpty(S3Prefix),
            S3PublicBaseUrl     = NullIfEmpty(S3PublicBaseUrl),
            GitHubReleaseTag    = NullIfEmpty(GitHubReleaseTag),
            GitHubCatalogTag    = NullIfEmpty(GitHubCatalogTag),
        };
        _templates.Save(template);
        RefreshTemplates(template.Id);
        TemplateStatus = $"Saved template “{template.Name}”.";
    }

    [RelayCommand(CanExecute = nameof(CanDeleteTemplate))]
    private void DeleteTemplate()
    {
        var t = SelectedTemplate;
        if (t is null || t.IsBuiltIn) return;
        var confirm = new ConfirmDialog("Delete Template",
            $"Delete the template “{t.Name}”?", "Delete") { Owner = System.Windows.Application.Current.MainWindow };
        if (confirm.ShowDialog() != true) return;
        _templates.Delete(t.Id);
        RefreshTemplates();
        TemplateStatus = $"Deleted template “{t.Name}”.";
    }

    private bool CanDeleteTemplate() => SelectedTemplate is { IsBuiltIn: false };

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public void Initialize(AppEntry entry, MainViewModel main)
    {
        _entry = entry;
        _main = main;

        var s = _settings.LoadAppSettings(entry.Name);
        OutputFolder        = s.OutputFolder        ?? string.Empty;
        DefaultCertPath     = s.DefaultCertPath      ?? string.Empty;
        DefaultCertPassword = s.DefaultCertPassword  ?? string.Empty;
        PackageExtension    = s.PackageExtension     ?? string.Empty;
        PackageNameTemplate = s.PackageNameTemplate  ?? string.Empty;

        PublishProvider = string.IsNullOrWhiteSpace(s.PublishProvider) ? "Ftp" : s.PublishProvider;

        FtpHost         = s.FtpHost         ?? string.Empty;
        FtpPort         = s.FtpPort == 0    ? 21 : s.FtpPort;
        FtpUsername     = s.FtpUsername     ?? string.Empty;
        FtpPassword      = s.FtpPassword     ?? string.Empty;
        FtpRemotePath   = s.FtpRemotePath   ?? string.Empty;
        BaseDownloadUrl = s.BaseDownloadUrl ?? string.Empty;

        SftpHost            = s.SftpHost            ?? string.Empty;
        SftpPort            = s.SftpPort == 0 ? 22 : s.SftpPort;
        SftpUsername        = s.SftpUsername        ?? string.Empty;
        SftpPassword        = s.SftpPassword        ?? string.Empty;
        SftpRemotePath      = s.SftpRemotePath      ?? string.Empty;
        SftpBaseDownloadUrl = s.SftpBaseDownloadUrl ?? string.Empty;

        S3Endpoint      = s.S3Endpoint      ?? string.Empty;
        S3Region        = s.S3Region        ?? string.Empty;
        S3Bucket        = s.S3Bucket        ?? string.Empty;
        S3AccessKey     = s.S3AccessKey     ?? string.Empty;
        S3SecretKey     = s.S3SecretKey     ?? string.Empty;
        S3Prefix        = s.S3Prefix        ?? string.Empty;
        S3PublicBaseUrl = s.S3PublicBaseUrl ?? string.Empty;

        GitHubReleaseTag = string.IsNullOrWhiteSpace(s.GitHubReleaseTag) ? "v{version}" : s.GitHubReleaseTag;
        GitHubCatalogTag = string.IsNullOrWhiteSpace(s.GitHubCatalogTag) ? "updates" : s.GitHubCatalogTag;

        UseStoreCert           = s.UseStoreCert;
        SelectedStoreThumbprint = s.StoreCertThumbprint;

        GitHubRepo         = s.GitHubRepo         ?? string.Empty;
        GitHubToken        = s.GitHubToken        ?? string.Empty;
        GitHubLocalPath    = s.GitHubLocalPath    ?? string.Empty;
        GitHubBuildCommand = s.GitHubBuildCommand ?? string.Empty;
        GitHubArtifactPath = s.GitHubArtifactPath ?? string.Empty;

        RefreshStoreCerts();
        RefreshTemplates();
    }

    private void RefreshStoreCerts()
    {
        StoreCertificates.Clear();
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        try
        {
            store.Open(OpenFlags.ReadOnly);
            foreach (var cert in store.Certificates)
            {
                using (cert)
                    StoreCertificates.Add(StoreCertInfo.FromX509(cert));
            }
        }
        catch { /* store not available — leave list empty */ }
        store.Close();

        if (SelectedStoreThumbprint is not null &&
            !StoreCertificates.Any(s => s.Thumbprint == SelectedStoreThumbprint))
            SelectedStoreThumbprint = null;
    }

    [RelayCommand]
    private void BrowseOutputFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select Output Base Folder" };
        if (dlg.ShowDialog() == true)
            OutputFolder = dlg.FolderName;
    }

    [RelayCommand]
    private void ClearOutputFolder() => OutputFolder = string.Empty;

    [RelayCommand]
    private void BrowseCert()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Default Certificate",
            Filter = "PFX Certificate (*.pfx)|*.pfx|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true)
            DefaultCertPath = dlg.FileName;
    }

    [RelayCommand]
    private void ClearCert()
    {
        DefaultCertPath     = string.Empty;
        DefaultCertPassword = string.Empty;
    }

    [RelayCommand]
    private void RefreshStoreCertList()
    {
        RefreshStoreCerts();
        if (StoreCertificates.Count > 0 && string.IsNullOrWhiteSpace(SelectedStoreThumbprint))
            SelectedStoreThumbprint = StoreCertificates[0].Thumbprint;
    }

    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        IsTestingConnection = true;
        ConnectionTestResult = string.Empty;
        try
        {
            ConnectionTestResult = await _publish.TestAsync(BuildSettings());
        }
        catch (Exception ex) { ConnectionTestResult = $"✗ {ex.Message}"; }
        finally
        {
            IsTestingConnection = false;
        }
    }

    private bool CanTestConnection() => !IsTestingConnection;

    [RelayCommand(CanExecute = nameof(CanTestGitHub))]
    private async Task TestGitHubAsync()
    {
        IsTestingGitHub = true;
        GitHubTestResult = string.Empty;
        try
        {
            // Per-app token overrides the account connection; otherwise use the global token.
            var token = string.IsNullOrWhiteSpace(GitHubToken) ? _settings.Global.GitHubToken : GitHubToken;
            GitHubTestResult = await _github.ValidateAsync(GitHubRepo, token);
        }
        catch (Exception ex) { GitHubTestResult = $"✗ {ex.Message}"; }
        finally { IsTestingGitHub = false; }
    }

    private bool CanTestGitHub() => !IsTestingGitHub && !string.IsNullOrWhiteSpace(GitHubRepo);

    [RelayCommand]
    private void BrowseGitHubLocalPath()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select the local repo folder" };
        if (dlg.ShowDialog() == true) GitHubLocalPath = dlg.FolderName;
    }

    [RelayCommand]
    private void BrowseGitHubArtifactPath()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select the build artifact folder" };
        if (dlg.ShowDialog() == true) GitHubArtifactPath = dlg.FolderName;
    }

    // Builds an AppSettings snapshot from the current fields (used by both Save and Test connection).
    private AppSettings BuildSettings()
    {
        var ext = PackageExtension.TrimStart('.').Trim();
        return new AppSettings
        {
            OutputFolder        = string.IsNullOrWhiteSpace(OutputFolder)        ? null : OutputFolder,
            DefaultCertPath     = string.IsNullOrWhiteSpace(DefaultCertPath)     ? null : DefaultCertPath,
            DefaultCertPassword = string.IsNullOrWhiteSpace(DefaultCertPassword) ? null : DefaultCertPassword,
            PackageExtension    = string.IsNullOrWhiteSpace(ext)                 ? null : ext,
            PackageNameTemplate = string.IsNullOrWhiteSpace(PackageNameTemplate) ? null : PackageNameTemplate.Trim(),

            PublishProvider = string.IsNullOrWhiteSpace(PublishProvider) ? null : PublishProvider,

            FtpHost         = string.IsNullOrWhiteSpace(FtpHost)         ? null : FtpHost,
            FtpPort         = FtpPort <= 0 ? 21 : FtpPort,
            FtpUsername     = string.IsNullOrWhiteSpace(FtpUsername)     ? null : FtpUsername,
            FtpPassword     = string.IsNullOrWhiteSpace(FtpPassword)     ? null : FtpPassword,
            FtpRemotePath   = string.IsNullOrWhiteSpace(FtpRemotePath)   ? null : FtpRemotePath,
            BaseDownloadUrl = string.IsNullOrWhiteSpace(BaseDownloadUrl) ? null : BaseDownloadUrl,

            SftpHost            = string.IsNullOrWhiteSpace(SftpHost)            ? null : SftpHost,
            SftpPort            = SftpPort <= 0 ? 22 : SftpPort,
            SftpUsername        = string.IsNullOrWhiteSpace(SftpUsername)        ? null : SftpUsername,
            SftpPassword        = string.IsNullOrWhiteSpace(SftpPassword)        ? null : SftpPassword,
            SftpRemotePath      = string.IsNullOrWhiteSpace(SftpRemotePath)      ? null : SftpRemotePath,
            SftpBaseDownloadUrl = string.IsNullOrWhiteSpace(SftpBaseDownloadUrl) ? null : SftpBaseDownloadUrl,

            S3Endpoint      = string.IsNullOrWhiteSpace(S3Endpoint)      ? null : S3Endpoint,
            S3Region        = string.IsNullOrWhiteSpace(S3Region)        ? null : S3Region,
            S3Bucket        = string.IsNullOrWhiteSpace(S3Bucket)        ? null : S3Bucket,
            S3AccessKey     = string.IsNullOrWhiteSpace(S3AccessKey)     ? null : S3AccessKey,
            S3SecretKey     = string.IsNullOrWhiteSpace(S3SecretKey)     ? null : S3SecretKey,
            S3Prefix        = string.IsNullOrWhiteSpace(S3Prefix)        ? null : S3Prefix,
            S3PublicBaseUrl = string.IsNullOrWhiteSpace(S3PublicBaseUrl) ? null : S3PublicBaseUrl,

            GitHubReleaseTag = string.IsNullOrWhiteSpace(GitHubReleaseTag) ? null : GitHubReleaseTag.Trim(),
            GitHubCatalogTag = string.IsNullOrWhiteSpace(GitHubCatalogTag) ? null : GitHubCatalogTag.Trim(),

            UseStoreCert        = UseStoreCert,
            StoreCertThumbprint = string.IsNullOrWhiteSpace(SelectedStoreThumbprint) ? null : SelectedStoreThumbprint,

            GitHubRepo         = string.IsNullOrWhiteSpace(GitHubRepo)         ? null : GitHubRepo.Trim(),
            GitHubToken        = string.IsNullOrWhiteSpace(GitHubToken)        ? null : GitHubToken,
            GitHubLocalPath    = string.IsNullOrWhiteSpace(GitHubLocalPath)    ? null : GitHubLocalPath,
            GitHubBuildCommand = string.IsNullOrWhiteSpace(GitHubBuildCommand) ? null : GitHubBuildCommand,
            GitHubArtifactPath = string.IsNullOrWhiteSpace(GitHubArtifactPath) ? null : GitHubArtifactPath,
        };
    }

    [RelayCommand]
    private void Save()
    {
        _settings.SaveAppSettings(_entry.Name, BuildSettings());
        // The publish target may have changed — drop the cached dashboard status so it re-checks.
        _connCache.Invalidate(_entry.Name);
        _main.NavigateToDetail(_entry);
    }

    [RelayCommand]
    private void Cancel() => _main.NavigateToDetail(_entry);
}
