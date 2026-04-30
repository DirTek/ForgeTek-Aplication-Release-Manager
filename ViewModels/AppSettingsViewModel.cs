using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class AppSettingsViewModel : ObservableObject
{
    private readonly AppEntry _entry;
    private readonly MainViewModel _main;
    private readonly SettingsService _settings;
    private readonly FtpService _ftp = new();

    public string AppName => _entry.Name;
    public string DefaultOutputBase => _settings.GetDefaultOutputBase(_entry.Name);

    // ── Global cert override ──────────────────────────────────────────────
    public bool   GlobalCertOverrideActive   => _settings.Global.UseGlobalCert
                                             && !string.IsNullOrWhiteSpace(_settings.Global.GlobalCertPath)
                                             && File.Exists(_settings.Global.GlobalCertPath);
    public bool   GlobalCertOverrideInactive => !GlobalCertOverrideActive;
    public string GlobalCertOverrideName     => GlobalCertOverrideActive
                                                ? Path.GetFileName(_settings.Global.GlobalCertPath!)
                                                : string.Empty;

    [ObservableProperty] private string _outputFolder = string.Empty;
    [ObservableProperty] private string _defaultCertPath = string.Empty;

    // PasswordBox handled via code-behind
    public string DefaultCertPassword { get; set; } = string.Empty;

    [ObservableProperty] private string _packageExtension = string.Empty;

    // ── FTP ──────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private string _ftpHost = string.Empty;

    [ObservableProperty] private int _ftpPort = 21;
    [ObservableProperty] private string _ftpUsername = string.Empty;

    // PasswordBox handled via code-behind
    public string FtpPassword { get; set; } = string.Empty;

    [ObservableProperty] private string _ftpRemotePath = string.Empty;
    [ObservableProperty] private string _baseDownloadUrl = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool _isTestingConnection;

    [ObservableProperty] private string _connectionTestResult = string.Empty;

    public AppSettingsViewModel(AppEntry entry, MainViewModel main, SettingsService settings)
    {
        _entry = entry;
        _main = main;
        _settings = settings;

        var s = settings.LoadAppSettings(entry.Name);
        _outputFolder       = s.OutputFolder        ?? string.Empty;
        _defaultCertPath    = s.DefaultCertPath     ?? string.Empty;
        DefaultCertPassword = s.DefaultCertPassword ?? string.Empty;
        _packageExtension   = s.PackageExtension    ?? string.Empty;

        _ftpHost         = s.FtpHost         ?? string.Empty;
        _ftpPort         = s.FtpPort == 0    ? 21 : s.FtpPort;
        _ftpUsername     = s.FtpUsername     ?? string.Empty;
        FtpPassword      = s.FtpPassword     ?? string.Empty;
        _ftpRemotePath   = s.FtpRemotePath   ?? string.Empty;
        _baseDownloadUrl = s.BaseDownloadUrl ?? string.Empty;
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
        });
        _main.NavigateToDetail(_entry);
    }

    [RelayCommand]
    private void Cancel() => _main.NavigateToDetail(_entry);
}
