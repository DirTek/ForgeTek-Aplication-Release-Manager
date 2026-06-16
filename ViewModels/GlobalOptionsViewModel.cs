using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Dialogs;
using ForgeTekUpdatePackager.Helpers;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class GlobalOptionsViewModel : ObservableObject
{
    private MainViewModel _main = null!;
    private readonly ISettingsService    _settings;
    private readonly ICertificateService _certService;
    private readonly IBackupService      _backupService;
    private readonly IThemeService       _theme;
    private readonly IUserService        _userService;
    private CancellationTokenSource?    _cts;
    private bool _suppressThemeApply;

    public ISessionService Session { get; }

    [ObservableProperty] private string _companyName = string.Empty;

    // ── Category navigation (driven by the sidebar) ───────────────────────
    // "Users" appears only to admins (and on first run, when no users exist yet).
    public string[] Categories => Session.CanManageUsers
        ? new[] { "General", "GitHub", "Signing", "Backup & Data", "Logs", "Users" }
        : new[] { "General", "GitHub", "Signing", "Backup & Data", "Logs" };

    [ObservableProperty] private string _selectedCategory = "General";

    partial void OnSelectedCategoryChanged(string value)
    {
        if (value == "Logs") RefreshLogs();
    }

    // ── Appearance ────────────────────────────────────────────────────────
    public string[] ThemeOptions { get; } = { "System", "Light", "Dark" };

    [ObservableProperty] private string _selectedTheme = "Dark";

    partial void OnSelectedThemeChanged(string value)
    {
        if (_suppressThemeApply) return;
        _theme.Apply(value);   // applies live + persists the preference
    }

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

    private readonly IProtectionStateService _protection;
    private readonly IGitHubAuthService _githubAuth;
    private readonly ILogService _log;

    public GlobalOptionsViewModel(ISettingsService settings, ICertificateService certService,
        IBackupService backupService, IThemeService theme, IUserService userService, ISessionService session,
        IProtectionStateService protection, IGitHubAuthService githubAuth, ILogService log)
    {
        _settings = settings;
        _certService = certService;
        _backupService = backupService;
        _theme = theme;
        _userService = userService;
        Session = session;
        _protection = protection;
        _githubAuth = githubAuth;
        _log = log;

        GenerateLog.CollectionChanged += (_, _) => HasGenerateLog = GenerateLog.Count > 0;
    }

    // ── Logs ──────────────────────────────────────────────────────────────
    public ObservableCollection<string> LogEntries { get; } = [];
    [ObservableProperty] private bool _hasLogEntries;

    [RelayCommand]
    private void RefreshLogs()
    {
        LogEntries.Clear();
        foreach (var line in _log.ReadRecent(500))
            LogEntries.Add(line);
        HasLogEntries = LogEntries.Count > 0;
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_log.LogFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _log.LogFolder,
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }

    public void Initialize(MainViewModel main)
    {
        _main = main;

        LoadAvailableCerts();
        RefreshStoreCerts();
        RefreshUsers();

        GitHubClientId    = _settings.Global.GitHubClientId ?? string.Empty;
        GitHubLogin       = _settings.Global.GitHubLogin    ?? string.Empty;
        IsGitHubConnected = !string.IsNullOrWhiteSpace(_settings.Global.GitHubToken);

        _suppressThemeApply = true;
        SelectedTheme = _theme.Current;
        _suppressThemeApply = false;

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

    /// <summary>Returns true if the save enabled protection for the first time (caller restarts).</summary>
    private bool PersistSettings()
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
        g.GitHubClientId     = string.IsNullOrWhiteSpace(GitHubClientId) ? null : GitHubClientId.Trim();
        _settings.SaveGlobal();

        return CommitUsers();
    }

    [RelayCommand]
    private void Save() { if (PersistSettings()) RestartApp(); }

    [RelayCommand]
    private void SaveAndClose()
    {
        if (PersistSettings()) { RestartApp(); return; }
        GoBack();
    }

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

    // ── GitHub account connection ─────────────────────────────────────────
    [ObservableProperty] private string _gitHubClientId = string.Empty;
    [ObservableProperty] private string _gitHubLogin = string.Empty;
    [ObservableProperty] private bool _isGitHubConnected;
    [ObservableProperty] private string? _gitHubMessage;

    [RelayCommand]
    private void ConnectGitHub()
    {
        GitHubMessage = null;
        if (string.IsNullOrWhiteSpace(GitHubClientId))
        {
            GitHubMessage = "Enter your OAuth App Client ID first (with Device Flow enabled).";
            return;
        }

        var dlg = new GitHubConnectWindow(_githubAuth, GitHubClientId.Trim())
        {
            Owner = Application.Current.MainWindow,
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Token))
        {
            _settings.Global.GitHubClientId = GitHubClientId.Trim();
            _settings.Global.GitHubToken    = dlg.Token;
            _settings.Global.GitHubLogin    = dlg.Login;
            _settings.SaveGlobal();   // persist immediately so a fresh token isn't lost

            GitHubLogin = dlg.Login ?? string.Empty;
            IsGitHubConnected = true;
            GitHubMessage = $"Connected as @{dlg.Login}.";
        }
    }

    [RelayCommand]
    private void DisconnectGitHub()
    {
        _settings.Global.GitHubToken = null;
        _settings.Global.GitHubLogin = null;
        _settings.SaveGlobal();
        GitHubLogin = string.Empty;
        IsGitHubConnected = false;
        GitHubMessage = "Disconnected.";
    }

    // Set by the view's PasswordBox handler.
    public string GitHubPatValue { get; set; } = string.Empty;

    [RelayCommand]
    private async Task UseGitHubToken()
    {
        GitHubMessage = null;
        if (string.IsNullOrWhiteSpace(GitHubPatValue))
        {
            GitHubMessage = "Paste a personal access token first.";
            return;
        }
        try
        {
            // Validates the token and gives us the @login to display.
            var login = await _githubAuth.GetLoginAsync(GitHubPatValue.Trim());
            _settings.Global.GitHubToken = GitHubPatValue.Trim();
            _settings.Global.GitHubLogin = login;
            _settings.SaveGlobal();
            GitHubLogin = login;
            IsGitHubConnected = true;
            GitHubPatValue = string.Empty;
            GitHubMessage = $"Connected as @{login}.";
        }
        catch (Exception ex) { GitHubMessage = $"Token rejected: {ex.Message}"; }
    }

    // ── Users (admin) ─────────────────────────────────────────────────────
    public ObservableCollection<UserRowVm> Users { get; } = [];
    public UserRole[] RoleOptions { get; } = Enum.GetValues<UserRole>();

    public bool ProtectionEnabled => _userService.HasAnyUsers;

    [ObservableProperty] private string _newUsername = string.Empty;
    [ObservableProperty] private UserRole _newUserRole = UserRole.Publisher;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedUser))]
    private UserRowVm? _selectedUser;

    [ObservableProperty] private string? _userMessage;

    public bool HasSelectedUser => SelectedUser is not null;

    // Set by the view's PasswordBox handlers (PasswordBox.Password can't be bound).
    public string NewUserPassword { get; set; } = string.Empty;
    public string ResetPasswordValue { get; set; } = string.Empty;

    // Changes are staged in the working set and only written on Save / Save and close.
    private void RefreshUsers()
    {
        Users.Clear();
        foreach (var u in _userService.GetAll().OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase))
        {
            var isCurrent = string.Equals(u.Username, Session.Current?.Username, StringComparison.OrdinalIgnoreCase);
            Users.Add(new UserRowVm(u, isCurrent));
        }
        OnPropertyChanged(nameof(ProtectionEnabled));
    }

    [RelayCommand]
    private void AddUser()
    {
        UserMessage = null;
        var name = NewUsername.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(NewUserPassword))
        {
            UserMessage = "Username and password are required.";
            return;
        }
        if (Users.Any(r => string.Equals(r.Username, name, StringComparison.OrdinalIgnoreCase)))
        {
            UserMessage = $"A user named '{name}' already exists.";
            return;
        }

        // The very first account is forced to Admin so protection always starts with an admin.
        var firstAccount = Users.Count == 0;
        Users.Add(new UserRowVm(name, firstAccount ? UserRole.Admin : NewUserRole, NewUserPassword));

        NewUsername = string.Empty;
        NewUserPassword = string.Empty;
        UserMessage = firstAccount
            ? "Administrator staged. Click “Save and close” to enable protection — the app will restart. " +
              "Tip: create a backup afterwards (Backup & Data) so you can recover if the user database is ever lost."
            : $"“{name}” staged. Click Save to apply.";
    }

    [RelayCommand]
    private void DeleteUser(UserRowVm? row)
    {
        if (row is null) return;
        UserMessage = null;
        if (string.Equals(row.Username, Session.Current?.Username, StringComparison.OrdinalIgnoreCase))
        {
            UserMessage = "You can't delete the account you're signed in as.";
            return;
        }
        if (row.Role == UserRole.Admin && Users.Count(r => r.Role == UserRole.Admin) <= 1)
        {
            UserMessage = "Can't remove the last administrator.";
            return;
        }
        Users.Remove(row);
    }

    [RelayCommand]
    private void ResetUserPassword(UserRowVm? row)
    {
        if (row is null) return;
        UserMessage = null;
        if (string.IsNullOrWhiteSpace(ResetPasswordValue))
        {
            UserMessage = "Enter a new password first.";
            return;
        }
        row.PendingPassword = ResetPasswordValue;
        ResetPasswordValue = string.Empty;
        UserMessage = $"Password change staged for {row.Username}. Click Save to apply.";
    }

    /// <summary>Writes the staged user changes to disk. Returns true when this newly enabled
    /// protection (the caller then restarts so the login gate engages).</summary>
    private bool CommitUsers()
    {
        var wasProtected = _userService.HasAnyUsers;

        var working = Users.Select(r => r.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in _userService.GetAll().ToList())
            if (!working.Contains(existing.Username))
                _userService.Delete(existing.Username);

        foreach (var r in Users)
        {
            if (r.IsNew)
            {
                _userService.Create(r.Username, r.PendingPassword ?? string.Empty, r.Role);
            }
            else
            {
                _userService.SetRole(r.Username, r.Role);
                if (!string.IsNullOrEmpty(r.PendingPassword))
                    _userService.SetPassword(r.Username, r.PendingPassword);
            }
        }

        RefreshUsers();

        // Keep the tamper-evident marker in sync with the real protection state.
        if (_userService.HasAnyUsers) _protection.Mark();
        else _protection.Clear();

        return !wasProtected && _userService.HasAnyUsers;
    }

    private static void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (exe is not null) System.Diagnostics.Process.Start(exe);
        Application.Current.Shutdown();
    }
}

/// <summary>A staged user row in the admin Users list. Role/password changes apply only on Save.</summary>
public partial class UserRowVm : ObservableObject
{
    public string Username { get; }
    public bool IsCurrent { get; }
    public bool IsNew { get; }

    /// <summary>A new password to apply on Save (set for new users, or when resetting).</summary>
    public string? PendingPassword { get; set; }

    [ObservableProperty] private UserRole _role;

    /// <summary>Existing user loaded from storage.</summary>
    public UserRowVm(AppUser user, bool isCurrent)
    {
        Username = user.Username;
        IsCurrent = isCurrent;
        _role = user.Role;
    }

    /// <summary>A new (pending) user, created in memory until Save.</summary>
    public UserRowVm(string username, UserRole role, string password)
    {
        Username = username;
        IsNew = true;
        _role = role;
        PendingPassword = password;
    }
}
