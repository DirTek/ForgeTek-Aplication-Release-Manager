using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Helpers;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class AppSettingsViewModel : ObservableObject
{
    private MainViewModel _main = null!;
    private AppEntry _entry = null!;
    private readonly ISettingsService _settings;
    private readonly IFtpService _ftp;

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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private string _ftpHost = string.Empty;

    [ObservableProperty] private int _ftpPort = 21;
    [ObservableProperty] private string _ftpUsername = string.Empty;

    public string FtpPassword { get; set; } = string.Empty;

    [ObservableProperty] private string _ftpRemotePath = string.Empty;
    [ObservableProperty] private string _baseDownloadUrl = string.Empty;

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

    public AppSettingsViewModel(ISettingsService settings, IFtpService ftp, IGitHubService github)
    {
        _settings = settings;
        _ftp = ftp;
        _github = github;
    }

    public void Initialize(AppEntry entry, MainViewModel main)
    {
        _entry = entry;
        _main = main;

        var s = _settings.LoadAppSettings(entry.Name);
        OutputFolder        = s.OutputFolder        ?? string.Empty;
        DefaultCertPath     = s.DefaultCertPath      ?? string.Empty;
        DefaultCertPassword = s.DefaultCertPassword  ?? string.Empty;
        PackageExtension    = s.PackageExtension     ?? string.Empty;

        FtpHost         = s.FtpHost         ?? string.Empty;
        FtpPort         = s.FtpPort == 0    ? 21 : s.FtpPort;
        FtpUsername     = s.FtpUsername     ?? string.Empty;
        FtpPassword      = s.FtpPassword     ?? string.Empty;
        FtpRemotePath   = s.FtpRemotePath   ?? string.Empty;
        BaseDownloadUrl = s.BaseDownloadUrl ?? string.Empty;

        UseStoreCert           = s.UseStoreCert;
        SelectedStoreThumbprint = s.StoreCertThumbprint;

        GitHubRepo         = s.GitHubRepo         ?? string.Empty;
        GitHubToken        = s.GitHubToken        ?? string.Empty;
        GitHubLocalPath    = s.GitHubLocalPath    ?? string.Empty;
        GitHubBuildCommand = s.GitHubBuildCommand ?? string.Empty;
        GitHubArtifactPath = s.GitHubArtifactPath ?? string.Empty;

        RefreshStoreCerts();
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
            ConnectionTestResult = await Task.Run(() =>
                _ftp.TestConnectionAsync(FtpHost, FtpPort, FtpUsername, FtpPassword));
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    private bool CanTestConnection() => !IsTestingConnection && !string.IsNullOrWhiteSpace(FtpHost);

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

    [RelayCommand]
    private void Save()
    {
        var ext = PackageExtension.TrimStart('.').Trim();

        _settings.SaveAppSettings(_entry.Name, new AppSettings
        {
            OutputFolder        = string.IsNullOrWhiteSpace(OutputFolder)        ? null : OutputFolder,
            DefaultCertPath     = string.IsNullOrWhiteSpace(DefaultCertPath)     ? null : DefaultCertPath,
            DefaultCertPassword = string.IsNullOrWhiteSpace(DefaultCertPassword) ? null : DefaultCertPassword,
            PackageExtension    = string.IsNullOrWhiteSpace(ext)                 ? null : ext,

            FtpHost         = string.IsNullOrWhiteSpace(FtpHost)         ? null : FtpHost,
            FtpPort         = FtpPort <= 0 ? 21 : FtpPort,
            FtpUsername     = string.IsNullOrWhiteSpace(FtpUsername)     ? null : FtpUsername,
            FtpPassword     = string.IsNullOrWhiteSpace(FtpPassword)     ? null : FtpPassword,
            FtpRemotePath   = string.IsNullOrWhiteSpace(FtpRemotePath)   ? null : FtpRemotePath,
            BaseDownloadUrl = string.IsNullOrWhiteSpace(BaseDownloadUrl) ? null : BaseDownloadUrl,

            UseStoreCert        = UseStoreCert,
            StoreCertThumbprint = string.IsNullOrWhiteSpace(SelectedStoreThumbprint) ? null : SelectedStoreThumbprint,

            GitHubRepo         = string.IsNullOrWhiteSpace(GitHubRepo)         ? null : GitHubRepo.Trim(),
            GitHubToken        = string.IsNullOrWhiteSpace(GitHubToken)        ? null : GitHubToken,
            GitHubLocalPath    = string.IsNullOrWhiteSpace(GitHubLocalPath)    ? null : GitHubLocalPath,
            GitHubBuildCommand = string.IsNullOrWhiteSpace(GitHubBuildCommand) ? null : GitHubBuildCommand,
            GitHubArtifactPath = string.IsNullOrWhiteSpace(GitHubArtifactPath) ? null : GitHubArtifactPath,
        });
        _main.NavigateToDetail(_entry);
    }

    [RelayCommand]
    private void Cancel() => _main.NavigateToDetail(_entry);
}
