using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Dialogs;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class GlobalOptionsViewModel : ObservableObject
{
    private readonly MainViewModel      _main;
    private readonly SettingsService    _settings;
    private readonly CertificateService _certService = new();
    private readonly BackupService      _backupService = new();
    private CancellationTokenSource?    _cts;

    // ── Global cert settings ───────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseGlobalCertHint))]
    private bool _useGlobalCert;

    /// <summary>
    /// Filename only (e.g. "MyCert.pfx"). Full path is derived via CertificateFolderPath.
    /// Null means no certificate is selected.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGlobalCert))]
    [NotifyPropertyChangedFor(nameof(GlobalCertFileName))]
    [NotifyCanExecuteChangedFor(nameof(ClearGlobalCertCommand))]
    private string? _selectedCertFileName;

    // Stored via PasswordBox code-behind (PasswordBox doesn't support TwoWay binding)
    public string GlobalCertPassword { get; set; } = string.Empty;

    public bool   HasGlobalCert     => !string.IsNullOrWhiteSpace(SelectedCertFileName);
    public string GlobalCertFileName => SelectedCertFileName ?? string.Empty;

    public string CertificateFolderPath => Path.Combine(_settings.RootFolder, "Certificates");

    public string UseGlobalCertHint => UseGlobalCert
        ? "Signing step will use this certificate for every app — per-app settings are ignored."
        : "Each app uses its own certificate configured in App Settings.";

    // ── Available certificates (dropdown) ─────────────────────────────────

    public ObservableCollection<string> AvailableCerts { get; } = [];

    private void LoadAvailableCerts()
    {
        var dir = CertificateFolderPath;
        AvailableCerts.Clear();

        if (Directory.Exists(dir))
            foreach (var file in Directory.GetFiles(dir, "*.pfx")
                                          .Select(Path.GetFileName)
                                          .Where(f => f is not null)
                                          .Order()!)
                AvailableCerts.Add(file!);

        // Drop selection if the selected cert no longer exists in the folder
        if (SelectedCertFileName is not null && !AvailableCerts.Contains(SelectedCertFileName))
            SelectedCertFileName = null;
    }

    // ── Certificate generation ─────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCertCommand))]
    private string _subjectName = string.Empty;

    [ObservableProperty] private string _friendlyName = string.Empty;

    // Set by code-behind from the PasswordBox
    public string GeneratePassword { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCertCommand))]
    private bool _isGenerating;

    [ObservableProperty] private bool _hasGenerateLog;

    public ObservableCollection<string> GenerateLog { get; }

    // ── Constructor ────────────────────────────────────────────────────────

    public GlobalOptionsViewModel(MainViewModel main, SettingsService settings)
    {
        _main     = main;
        _settings = settings;

        GenerateLog = [];
        GenerateLog.CollectionChanged += (_, _) => HasGenerateLog = GenerateLog.Count > 0;

        LoadAvailableCerts();

        var g = settings.Global;
        _useGlobalCert     = g.UseGlobalCert;
        GlobalCertPassword = g.GlobalCertPassword ?? string.Empty;

        // Restore selection from saved path
        if (!string.IsNullOrWhiteSpace(g.GlobalCertPath))
        {
            var savedFileName = Path.GetFileName(g.GlobalCertPath);
            if (AvailableCerts.Contains(savedFileName))
                _selectedCertFileName = savedFileName;
        }
    }

    // ── Commands ───────────────────────────────────────────────────────────

    /// <summary>
    /// Copies a PFX from anywhere on disk into the Certificates folder and selects it.
    /// </summary>
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
                progress, _cts.Token);

            GenerateLog.Add(string.Empty);
            GenerateLog.Add($"✔  Saved to: {certPath}");

            // Refresh dropdown and auto-select the new cert
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

    [RelayCommand]
    private void Save()
    {
        var g = _settings.Global;
        g.UseGlobalCert = UseGlobalCert;
        g.GlobalCertPath = string.IsNullOrWhiteSpace(SelectedCertFileName)
            ? null
            : Path.Combine(CertificateFolderPath, SelectedCertFileName);
        g.GlobalCertPassword = string.IsNullOrWhiteSpace(GlobalCertPassword) ? null : GlobalCertPassword;
        _settings.SaveGlobal();
        GoBack();
    }

    [RelayCommand]
    private void CreateBackup()
    {
        new BackupDialog(_backupService, _settings.RootFolder, SettingsService.GlobalSettingsFilePath)
            { Owner = Application.Current.MainWindow }.ShowDialog();
    }

    [RelayCommand]
    private void Cancel() => GoBack();

    private void GoBack()
    {
        if (_main.SelectedApp is not null)
            _main.NavigateToDetail(_main.SelectedApp.Entry);
        else
            _main.NavigateToWelcome();
    }
}
