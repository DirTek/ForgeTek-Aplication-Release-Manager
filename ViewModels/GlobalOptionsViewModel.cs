using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Dialogs;
using ForgeTekUpdatePackager.Helpers;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class GlobalOptionsViewModel : ObservableObject
{
    private MainViewModel _main = null!;
    private readonly ISettingsService    _settings;
    private readonly ICertificateService _certService;
    private readonly IBackupService      _backupService;
    private CancellationTokenSource?    _cts;

    [ObservableProperty] private string _companyName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseGlobalCertHint))]
    private bool _useGlobalCert;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGlobalCert))]
    [NotifyPropertyChangedFor(nameof(GlobalCertFileName))]
    [NotifyCanExecuteChangedFor(nameof(ClearGlobalCertCommand))]
    private string? _selectedCertFileName;

    public string GlobalCertPassword { get; set; } = string.Empty;

    public bool   HasGlobalCert     => !string.IsNullOrWhiteSpace(SelectedCertFileName);
    public string GlobalCertFileName => SelectedCertFileName ?? string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStoreCert))]
    private bool _useStoreCert;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStoreCert))]
    private string? _selectedStoreThumbprint;

    [ObservableProperty] private bool _keepInCertStore;

    public bool   HasStoreCert => !string.IsNullOrWhiteSpace(SelectedStoreThumbprint);

    public ObservableCollection<StoreCertInfo> StoreCertificates { get; } = [];

    public string CertificateFolderPath => Path.Combine(_settings.RootFolder, "Certificates");

    public string UseGlobalCertHint => UseGlobalCert
        ? "Signing step will use this certificate for every app — per-app settings are ignored."
        : "Each app uses its own certificate configured in App Settings.";

    public ObservableCollection<string> AvailableCerts { get; } = [];

    private void LoadAvailableCerts()
    {
        var dir = CertificateFolderPath;
        AvailableCerts.Clear();

        if (Directory.Exists(dir))
            foreach (var file in Directory.GetFiles(dir, "*.pfx")
                                          .Select(Path.GetFileName)
                                          .OfType<string>()
                                          .Order())
                AvailableCerts.Add(file);

        if (SelectedCertFileName is not null && !AvailableCerts.Contains(SelectedCertFileName))
            SelectedCertFileName = null;
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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCertCommand))]
    private string _subjectName = string.Empty;

    [ObservableProperty] private string _friendlyName = string.Empty;

    public string GeneratePassword { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCertCommand))]
    private bool _isGenerating;

    [ObservableProperty] private bool _hasGenerateLog;

    public ObservableCollection<string> GenerateLog { get; } = [];

    public GlobalOptionsViewModel(ISettingsService settings, ICertificateService certService, IBackupService backupService)
    {
        _settings = settings;
        _certService = certService;
        _backupService = backupService;

        GenerateLog.CollectionChanged += (_, _) => HasGenerateLog = GenerateLog.Count > 0;
    }

    public void Initialize(MainViewModel main)
    {
        _main = main;

        LoadAvailableCerts();
        RefreshStoreCerts();

        var g = _settings.Global;
        CompanyName            = g.CompanyName;
        UseGlobalCert          = g.UseGlobalCert;
        UseStoreCert           = g.UseStoreCert;
        GlobalCertPassword     = g.GlobalCertPassword ?? string.Empty;
        SelectedStoreThumbprint = g.StoreCertThumbprint;
        KeepInCertStore        = g.KeepInCertStore;

        if (!string.IsNullOrWhiteSpace(g.GlobalCertPath))
        {
            var savedFileName = Path.GetFileName(g.GlobalCertPath);
            if (AvailableCerts.Contains(savedFileName))
                SelectedCertFileName = savedFileName;
        }
    }

    [RelayCommand]
    private void BrowseAndCopyCert()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title           = "Select PFX Certificate",
            Filter          = "PFX Certificate (*.pfx)|*.pfx|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        var sourcePath = dlg.FileName;
        var fileName   = Path.GetFileName(sourcePath);
        var destPath   = Path.Combine(CertificateFolderPath, fileName);

        try
        {
            Directory.CreateDirectory(CertificateFolderPath);
            File.Copy(sourcePath, destPath, overwrite: true);
            LoadAvailableCerts();
            SelectedCertFileName = fileName;
        }
        catch (Exception ex)
        {
            new AlertDialog("Copy Failed", $"Could not copy certificate:\n{ex.Message}")
                { Owner = Application.Current.MainWindow }.ShowDialog();
        }
    }

    [RelayCommand(CanExecute = nameof(CanClearGlobalCert))]
    private void ClearGlobalCert()
    {
        SelectedCertFileName = null;
        GlobalCertPassword   = string.Empty;
    }

    private bool CanClearGlobalCert() => SelectedCertFileName is not null;

    [RelayCommand]
    private void RefreshStoreCertList()
    {
        RefreshStoreCerts();
        if (StoreCertificates.Count > 0 && string.IsNullOrWhiteSpace(SelectedStoreThumbprint))
            SelectedStoreThumbprint = StoreCertificates[0].Thumbprint;
    }

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateCertAsync()
    {
        IsGenerating = true;
        GenerateLog.Clear();

        var progress = new Progress<string>(msg => GenerateLog.Add(msg));

        _cts = new CancellationTokenSource();
        try
        {
            var certPath = await _certService.GenerateSelfSignedCertAsync(
                SubjectName, FriendlyName, GeneratePassword, CertificateFolderPath,
                KeepInCertStore, progress, _cts.Token);

            GenerateLog.Add(string.Empty);
            GenerateLog.Add($"✔  Saved to: {certPath}");

            LoadAvailableCerts();
            SelectedCertFileName = Path.GetFileName(certPath);
            GlobalCertPassword   = GeneratePassword;
        }
        catch (OperationCanceledException) { GenerateLog.Add("Cancelled."); }
        catch (Exception ex)              { GenerateLog.Add($"✗  {ex.Message}"); }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsGenerating = false;
        }
    }

    private bool CanGenerate()
        => !IsGenerating
           && !string.IsNullOrWhiteSpace(SubjectName)
           && !string.IsNullOrWhiteSpace(GeneratePassword);

    private void PersistSettings()
    {
        var g = _settings.Global;
        g.CompanyName        = CompanyName;
        g.UseGlobalCert      = UseGlobalCert;
        g.UseStoreCert       = UseStoreCert;
        g.GlobalCertPath = string.IsNullOrWhiteSpace(SelectedCertFileName)
            ? null
            : Path.Combine(CertificateFolderPath, SelectedCertFileName);
        g.GlobalCertPassword = string.IsNullOrWhiteSpace(GlobalCertPassword) ? null : GlobalCertPassword;
        g.StoreCertThumbprint = string.IsNullOrWhiteSpace(SelectedStoreThumbprint) ? null : SelectedStoreThumbprint;
        g.KeepInCertStore    = KeepInCertStore;
        _settings.SaveGlobal();
    }

    [RelayCommand]
    private void Save() => PersistSettings();

    [RelayCommand]
    private void SaveAndClose() { PersistSettings(); GoBack(); }

    [RelayCommand]
    private void Close() => GoBack();

    [RelayCommand]
    private void CreateBackup()
    {
        new BackupDialog(_backupService, _settings.RootFolder, SettingsService.GlobalSettingsFilePath)
            { Owner = Application.Current.MainWindow }.ShowDialog();
    }

    private void GoBack()
    {
        if (_main.SelectedApp is not null)
            _main.NavigateToDetail(_main.SelectedApp.Entry);
        else
            _main.NavigateToWelcome();
    }
}
