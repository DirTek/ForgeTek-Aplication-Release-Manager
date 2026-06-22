using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using ForgeTekApplicationReleaseManager.Config;
using ForgeTekApplicationReleaseManager.Data;
using ForgeTekApplicationReleaseManager.Dialogs;
using ForgeTekApplicationReleaseManager.Helpers;
using ForgeTekApplicationReleaseManager.Models;
using ForgeTekApplicationReleaseManager.Services;

namespace ForgeTekApplicationReleaseManager.ViewModels;

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
        ? new[] { "General", "GitHub", "Signing", "Backup & Data", "Database", "Logs", "Users" }
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

    /// <summary>When on (and protection is enabled), releases need Admin + QA Tester approval to publish.</summary>
    [ObservableProperty] private bool _requireReleaseApproval;

    // ── Database / connection mode ────────────────────────────────────────
    private readonly ConnectionConfig _connection = ConnectionConfig.Load();

    /// <summary>False = local SQLite (standalone); True = shared SQL Server (networked, multi-operator).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNetworkedSelected))]
    private bool _useNetworkedDatabase;

    public bool IsNetworkedSelected => UseNetworkedDatabase;

    /// <summary>Networked (shared SQL Server) is only offered once the deployment has at least one Admin,
    /// so a shared store always has an owner who can administer it (e.g. a solo operator syncing a PC and a laptop).</summary>
    public bool CanUseNetworked =>
        _userService.GetAll().Any(u => u.Role == UserRole.Admin);

    [ObservableProperty] private string _sqlServerConnectionString = string.Empty;
    [ObservableProperty] private string _connectionStatus = string.Empty;
    [ObservableProperty] private bool _isTestingConnection;

    /// <summary>The provider in effect for the current session (changes take effect after restart).</summary>
    public string CurrentStorageMode =>
        _connection.IsNetworked && !string.IsNullOrWhiteSpace(_connection.SqlServerConnectionString)
            ? "SQL Server (networked)" : "SQLite (local)";

    [RelayCommand]
    private async Task TestConnection()
    {
        if (string.IsNullOrWhiteSpace(SqlServerConnectionString))
        {
            ConnectionStatus = "Enter a SQL Server connection string first.";
            return;
        }
        IsTestingConnection = true;
        ConnectionStatus = "Testing…";
        try
        {
            var ok = await Task.Run(() =>
            {
                var opts = new DbContextOptionsBuilder<ForgeTekDbContext>()
                    .UseSqlServer(SqlServerConnectionString.Trim()).Options;
                using var ctx = new ForgeTekDbContext(opts);
                return ctx.Database.CanConnect();
            });
            ConnectionStatus = ok ? "✓ Connected successfully." : "✗ Could not connect (check the server and credentials).";
        }
        catch (Exception ex) { ConnectionStatus = $"✗ {ex.Message}"; }
        finally { IsTestingConnection = false; }
    }

    [RelayCommand]
    private void SaveConnection()
    {
        if (UseNetworkedDatabase && !CanUseNetworked)
        {
            MessageBox.Show(
                "Create at least one Admin user before switching to a shared SQL Server, so the shared store has an owner.",
                "Admin Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            UseNetworkedDatabase = false;
            return;
        }

        _connection.Mode = UseNetworkedDatabase ? StorageMode.Networked : StorageMode.Standalone;
        _connection.SqlServerConnectionString =
            string.IsNullOrWhiteSpace(SqlServerConnectionString) ? null : SqlServerConnectionString.Trim();
        _connection.Save();
        OnPropertyChanged(nameof(CurrentStorageMode));

        var target = _connection.IsNetworked ? "SQL Server (networked)" : "SQLite (local)";
        var restart = MessageBox.Show(
            $"Connection settings saved.\n\nThe app must restart to switch to {target}. Restart now?",
            "Restart Required", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (restart == MessageBoxResult.Yes)
        {
            var exe = Environment.ProcessPath;
            if (exe is not null) System.Diagnostics.Process.Start(exe);
            System.Windows.Application.Current.Shutdown();
        }
    }

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
        IProtectionStateService protection, IGitHubAuthService githubAuth, ILogService log,
        Services.Storage.ISharedCertificateStore certStore, IDialogService dialog)
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
        _certStore = certStore;
        _dialog = dialog;

        GenerateLog.CollectionChanged += (_, _) => HasGenerateLog = GenerateLog.Count > 0;
    }

    // ── Shared certificates (networked: stored in the DB so operators can download/register them) ──
    private readonly Services.Storage.ISharedCertificateStore _certStore;
    private readonly IDialogService _dialog;

    /// <summary>Whether the shared-certificate UI applies (networked mode only).</summary>
    public bool ShowSharedCertificates => _certStore.IsShared;

    public ObservableCollection<SharedCertificate> SharedCertificates { get; } = [];

    private async void LoadSharedCerts()
    {
        if (!_certStore.IsShared) return;
        try
        {
            var list = await _certStore.ListAsync();
            SharedCertificates.Clear();
            foreach (var c in list) SharedCertificates.Add(c);
        }
        catch (Exception ex) { _log.Write("Certificates", $"Listing shared certificates failed: {ex.Message}"); }
    }

    /// <summary>Saves the .pfx to a file of the operator's choosing (the password is shared out-of-band).</summary>
    [RelayCommand]
    private async Task DownloadCert(SharedCertificate? cert)
    {
        if (cert is null) return;
        var path = _dialog.SaveFile("Save Certificate As", "Certificate (*.pfx)|*.pfx",
            $"{Sanitize(cert.Subject)}.pfx");
        if (path is null) return;
        try
        {
            var pfx = await _certStore.GetPfxAsync(cert.Id);
            if (pfx is null) { _dialog.Alert("Download Failed", "The certificate is no longer in the database."); return; }
            await File.WriteAllBytesAsync(path, pfx);
            _dialog.Alert("Certificate Saved",
                $"Saved to:\n{path}\n\nUse the certificate password (shared separately) to install or sign with it.");
        }
        catch (Exception ex) { _dialog.Alert("Download Failed", ex.Message); }
    }

    /// <summary>Imports the .pfx into this machine's personal store (prompting for the password) so signing
    /// can use it locally by thumbprint.</summary>
    [RelayCommand]
    private async Task RegisterCert(SharedCertificate? cert)
    {
        if (cert is null) return;
        var pwd = _dialog.PromptPassword("Register Certificate",
            $"Enter the password for '{cert.Subject}' to import it into your personal certificate store.");
        if (pwd is null) return;   // cancelled
        try
        {
            var pfx = await _certStore.GetPfxAsync(cert.Id);
            if (pfx is null) { _dialog.Alert("Register Failed", "The certificate is no longer in the database."); return; }
            await Task.Run(() => _certService.ImportToUserStore(pfx, pwd));
            RefreshStoreCerts();
            SelectedStoreThumbprint = cert.Thumbprint;
            UseStoreCert = true;
            _dialog.Alert("Certificate Registered",
                $"'{cert.Subject}' is now in your personal store. Select it under \"Use a certificate from the Windows store\" to sign with it.");
        }
        catch (Exception ex)
        {
            _dialog.Alert("Register Failed",
                $"Could not import the certificate. Check the password.\n\n{ex.Message}");
        }
    }

    private static string Sanitize(string subject)
        => string.Concat((subject ?? "certificate").Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');

    // ── Logs ──────────────────────────────────────────────────────────────
    public ObservableCollection<string> LogEntries { get; } = [];
    [ObservableProperty] private bool _hasLogEntries;

    /// <summary>The full range result, before the category filter is applied.</summary>
    private readonly List<string> _allLogLines = [];

    public const string AllCategories = "All categories";

    /// <summary>"All categories" + the distinct categories present in the loaded range.</summary>
    public ObservableCollection<string> LogCategories { get; } = [AllCategories];
    [ObservableProperty] private string _selectedLogCategory = AllCategories;

    partial void OnSelectedLogCategoryChanged(string value) => ApplyLogFilter();

    // Date range for the log viewer — defaults to today, changeable via the pickers or the preset buttons.
    [ObservableProperty] private DateTime _logFrom = DateTime.Today;
    [ObservableProperty] private DateTime _logTo = DateTime.Today;
    private bool _suppressLogRefresh;

    partial void OnLogFromChanged(DateTime value) { if (!_suppressLogRefresh) RefreshLogs(); }
    partial void OnLogToChanged(DateTime value)   { if (!_suppressLogRefresh) RefreshLogs(); }

    [RelayCommand] private void LogsToday()  => SetLogRange(DateTime.Today, DateTime.Today);
    [RelayCommand] private void LogsLast7()  => SetLogRange(DateTime.Today.AddDays(-6), DateTime.Today);
    [RelayCommand] private void LogsLast30() => SetLogRange(DateTime.Today.AddDays(-29), DateTime.Today);

    // Set both ends without refreshing twice mid-change.
    private void SetLogRange(DateTime from, DateTime to)
    {
        _suppressLogRefresh = true;
        LogFrom = from;
        LogTo = to;
        _suppressLogRefresh = false;
        RefreshLogs();
    }

    [RelayCommand]
    private void RefreshLogs()
    {
        var from = DateOnly.FromDateTime(LogFrom.Date);
        var to   = DateOnly.FromDateTime(LogTo.Date);

        _allLogLines.Clear();
        _allLogLines.AddRange(_log.ReadRange(from, to));

        // Rebuild the category list from what's actually in the range; keep the current pick if still valid.
        var previous = SelectedLogCategory;
        var cats = _allLogLines.Select(ExtractCategory).Where(c => c is not null).Distinct()
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        LogCategories.Clear();
        LogCategories.Add(AllCategories);
        foreach (var c in cats) LogCategories.Add(c!);

        // Assigning the property re-applies the filter via OnSelectedLogCategoryChanged when it changes;
        // the explicit call below covers the no-change case (e.g. still "All categories").
        SelectedLogCategory = LogCategories.Contains(previous) ? previous : AllCategories;
        ApplyLogFilter();
    }

    private void ApplyLogFilter()
    {
        LogEntries.Clear();
        var all = SelectedLogCategory == AllCategories;
        foreach (var line in _allLogLines)
            if (all || string.Equals(ExtractCategory(line), SelectedLogCategory, StringComparison.OrdinalIgnoreCase))
                LogEntries.Add(line);
        HasLogEntries = LogEntries.Count > 0;
    }

    // Lines look like "yyyy-MM-dd [HH:mm:ss.fff] [Category] message"; the category is the 2nd bracketed token.
    private static string? ExtractCategory(string line)
    {
        var first = line.IndexOf("] [", StringComparison.Ordinal);
        if (first < 0) return null;
        var start = first + 3;
        var end = line.IndexOf(']', start);
        return end < 0 ? null : line[start..end];
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
        LoadSharedCerts();
        RefreshLogs();   // show today's logs by default

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
        RequireReleaseApproval = g.RequireReleaseApproval;

        UseNetworkedDatabase     = _connection.IsNetworked;
        SqlServerConnectionString = _connection.SqlServerConnectionString ?? string.Empty;

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

            // Networked: share the .pfx through the DB so other operators can download/register it. The
            // password is NOT stored — it stays with whoever generated it and is shared out-of-band.
            if (_certStore.IsShared)
            {
                try
                {
                    var pfx        = await File.ReadAllBytesAsync(certPath, _cts.Token);
                    var thumbprint = _certService.ReadThumbprint(pfx, GeneratePassword);
                    await _certStore.SaveAsync(SubjectName, FriendlyName, thumbprint, pfx, Session.Current?.Username);
                    LoadSharedCerts();
                    GenerateLog.Add("✔  Shared to the database — other operators can download or register it.");
                    GenerateLog.Add("⚠  Record the password now; it is NOT stored in the database.");
                }
                catch (Exception ex) { GenerateLog.Add($"⚠  Could not share to the database: {ex.Message}"); }
            }
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
        g.RequireReleaseApproval = RequireReleaseApproval;
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

    [ObservableProperty] private bool _isRestoring;

    /// <summary>Re-imports a backup into the active store, then restarts so the EF caches reload.</summary>
    [RelayCommand]
    private async Task RestoreBackup()
    {
        var sharedWarning = _connection.IsNetworked && !string.IsNullOrWhiteSpace(_connection.SqlServerConnectionString)
            ? "\n\n⚠ You are connected to a SHARED SQL Server — this restore affects every operator's data."
            : string.Empty;
        if (!_dialog.Confirm("Restore From Backup",
                "This overwrites current data (apps, settings, users, certificates) with the backup's contents, " +
                "then restarts the app." + sharedWarning + "\n\nContinue?",
                "Choose Backup…"))
            return;

        var path = _dialog.OpenFile("Select a Backup ZIP", "Backup ZIP (*.zip)|*.zip|All files (*.*)|*.*");
        if (path is null) return;

        IsRestoring = true;
        try
        {
            // RestoreAsync already offloads to a background thread; await it directly.
            var progress = new Progress<string>(msg => _log.Write("Restore", msg));
            var users = await _backupService.RestoreAsync(path, progress, CancellationToken.None);
            _log.Write("Restore", $"Restore complete from {path} ({users} user(s)).");
            _dialog.Alert("Restore Complete",
                $"Restored {users} user(s) and all data.\n\nThe app will now restart to load it.");
            RestartApp();
        }
        catch (Exception ex)
        {
            _dialog.Alert("Restore Failed", ex.Message);
        }
        finally { IsRestoring = false; }
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
        OnPropertyChanged(nameof(CanUseNetworked));
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
