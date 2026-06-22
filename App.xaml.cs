using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Windows;
using ForgeTekApplicationReleaseManager.Data;
using ForgeTekApplicationReleaseManager.Services;
using ForgeTekApplicationReleaseManager.Services.Publishing;
using ForgeTekApplicationReleaseManager.Services.Security;
using ForgeTekApplicationReleaseManager.ViewModels;

namespace ForgeTekApplicationReleaseManager;

public partial class App : Application
{
    private IServiceProvider _services = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Catch unhandled UI-thread exceptions: log them and show a message instead of crashing,
        // so a single bad screen/data record can't take the whole app (and its unsaved state) down.
        DispatcherUnhandledException += (_, args) =>
        {
            try { _services?.GetService<ILogService>()?.Write("Crash", args.Exception.ToString()); } catch { }
            MessageBox.Show(args.Exception.Message, "Unexpected Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var services = new ServiceCollection();

        // ── Data layer: pick the EF Core provider + secret protector from the bootstrap config ──
        // Standalone = local SQLite + DPAPI (current-user). Networked = shared SQL Server, with secret
        // sharing deferred (NoSharedProtectorYet blocks plaintext credentials reaching the shared DB).
        var connection = Config.ConnectionConfig.Load();
        var networked  = connection.IsNetworked && !string.IsNullOrWhiteSpace(connection.SqlServerConnectionString);

        if (connection.IsNetworked && !networked)
            // Configured for networked but no usable connection string → fall back to local, fail-safe.
            System.Diagnostics.Debug.WriteLine("Networked mode set but no SQL Server connection string; using local SQLite.");

        var sqlitePath = string.IsNullOrWhiteSpace(connection.SqlitePath)
            ? Path.Combine(AppContext.BaseDirectory, "data", "forgetek.db")
            : connection.SqlitePath!;
        if (!networked) Directory.CreateDirectory(Path.GetDirectoryName(sqlitePath)!);

        services.AddDbContextFactory<Data.ForgeTekDbContext>(opt =>
        {
            if (networked) opt.UseSqlServer(connection.SqlServerConnectionString!);
            else           opt.UseSqlite($"Data Source={sqlitePath}");
        });

        if (networked) services.AddSingleton<ISecretProtector, NoSharedProtectorYet>();
        else           services.AddSingleton<ISecretProtector, DpapiSecretProtector>();

        // EF-backed stores (replace the legacy per-file services, which remain only as import sources).
        services.AddSingleton<ISettingsService, Services.Storage.EfSettingsService>();
        services.AddSingleton<IStorageService, Services.Storage.EfStorageService>();
        services.AddSingleton<IUserService, Services.Storage.EfUserService>();
        services.AddSingleton<ISetupStorageService, Services.Storage.EfSetupStorageService>();
        services.AddSingleton<IApprovalService, ApprovalService>();
        services.AddTransient<AppDataImporter>();

        // Source-file blob store: real (content-addressed, in the shared DB) when networked so versions can
        // be (re)packaged from any machine; a no-op standalone, where packaging reads source from local disk.
        if (networked) services.AddSingleton<Services.Storage.IFileBlobStore, Services.Storage.EfFileBlobStore>();
        else           services.AddSingleton<Services.Storage.IFileBlobStore, Services.Storage.NullFileBlobStore>();

        // Shared code-signing certificates live in the DB when networked so operators can download/register
        // them locally; standalone keeps certs as local files (no-op store).
        if (networked) services.AddSingleton<Services.Storage.ISharedCertificateStore, Services.Storage.EfSharedCertificateStore>();
        else           services.AddSingleton<Services.Storage.ISharedCertificateStore, Services.Storage.NullSharedCertificateStore>();

        // Protection marker: shared DB flag when networked, per-machine registry when standalone.
        if (networked) services.AddSingleton<IProtectionStateService, Services.Storage.EfProtectionStateService>();
        else           services.AddSingleton<IProtectionStateService, ProtectionStateService>();

        // Singletons (shared state or expensive to create)
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<IScannerService, ScannerService>();
        services.AddSingleton<ISigningService, SigningService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<ISessionService, SessionService>();
        services.AddSingleton<IGitHubService, GitHubService>();
        services.AddSingleton<IGitHubAuthService, GitHubAuthService>();

        // Dialog service (created per-need, wraps WPF dialogs)
        services.AddSingleton<IDialogService, DialogService>();

        // Transient (stateless, created per-need)
        services.AddTransient<IPackagingService, PackagingService>();
        services.AddTransient<IFtpService, FtpService>();
        services.AddTransient<IManifestService, ManifestService>();
        services.AddTransient<IUpdateCatalogService, UpdateCatalogService>();
        services.AddTransient<IPublishService, PublishService>();
        services.AddTransient<ICertificateService, CertificateService>();
        services.AddTransient<IBackupService, BackupService>();
        services.AddTransient<IChangelogService, ChangelogService>();

        // Setup services
        services.AddSingleton<ISettingsTemplateService, SettingsTemplateService>();
        services.AddSingleton<IConnectionStatusCache, ConnectionStatusCache>();
        services.AddSingleton<IWingetManifestService, WingetManifestService>();
        services.AddSingleton<IVulnerabilityScanService, VulnerabilityScanService>();
        services.AddSingleton<ILicenseScanService, LicenseScanService>();
        services.AddSingleton<ISourceCompareService, SourceCompareService>();
        services.AddTransient<ISetupService, SetupService>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<AppDetailViewModel>();
        services.AddTransient<ScanViewModel>();
        services.AddTransient<DiffViewModel>();
        services.AddTransient<PackageViewModel>();
        services.AddTransient<ReviseViewModel>();
        services.AddTransient<AppSettingsViewModel>();
        services.AddTransient<GlobalOptionsViewModel>();
        services.AddTransient<SetupViewModel>();

        _services = services.BuildServiceProvider();

        // Ensure the database AND tables exist. Unlike EnsureCreated() (which no-ops if the database
        // already exists, even when empty), this also creates the tables in a DBA-pre-created empty
        // database — the common SQL Server case where the app login lacks CREATE DATABASE rights.
        // The relational shape is stable; schema-volatile data lives inside the JSON payloads.
        if (!EnsureSchema())
        {
            Shutdown();
            return;
        }

        // On a fresh store, migrate any legacy JSON data.
        var importer = _services.GetRequiredService<AppDataImporter>();
        try { if (importer.NeedsImport()) importer.ImportFromFiles(); }
        catch (Exception ex) { _services.GetService<ILogService>()?.Write("Import", $"JSON import failed: {ex.Message}"); }

        // Apply the saved theme before any window is shown so it paints correctly on first render.
        _services.GetRequiredService<IThemeService>().ApplySaved();

        // Access control is opt-in. Protection is "on" when there are users OR a tamper-evident
        // marker says so — so deleting users.json alone can't silently disable the login.
        var users  = _services.GetRequiredService<IUserService>();
        var marker = _services.GetRequiredService<IProtectionStateService>();

        if (users.HasAnyUsers || marker.IsMarked)
        {
            // Don't let the app exit when a dialog closes (it's the only window open) — otherwise a
            // successful sign-in would shut down before the main window is created.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            if (!users.HasAnyUsers)
            {
                // Marker says protected but the user database is gone → fail closed.
                var lockout = new Dialogs.LockoutWindow(_services.GetRequiredService<IBackupService>());
                lockout.ShowDialog();   // restores + relaunches, or exits
                Shutdown();
                return;
            }

            var login = new Dialogs.LoginWindow(users);
            if (login.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
            _services.GetRequiredService<ISessionService>().SignIn(login.AuthenticatedUser!);
            marker.Mark();   // self-heal the marker in case only the registry side was cleared
        }

        var vm = _services.GetRequiredService<MainViewModel>();
        var window = new MainWindow(vm);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }

    /// <summary>
    /// Ensures the database and all tables exist for the configured provider. Creates the database when
    /// it's missing (and the login may), and — unlike <c>EnsureCreated()</c> — also creates the tables in
    /// an existing-but-empty database (the common SQL Server case where a DBA pre-creates the database and
    /// the app login can't). No-ops when the schema is already present. Returns false on failure.
    /// </summary>
    private bool EnsureSchema()
    {
        try
        {
            using var db = _services.GetRequiredService<IDbContextFactory<ForgeTekDbContext>>().CreateDbContext();
            var creator = db.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>();
            if (!creator.Exists())    creator.Create();        // CREATE DATABASE / the SQLite file
            if (!creator.HasTables()) creator.CreateTables();  // CREATE TABLE … for the model
            EnsureFileBlobsTable(db);                          // add tables introduced after first release
            EnsureCertificatesTable(db);
            return true;
        }
        catch (Exception ex)
        {
            _services.GetService<ILogService>()?.Write("Startup", $"Database initialization failed: {ex}");
            MessageBox.Show(
                "Could not initialize the database.\n\n" + ex.Message +
                "\n\nCheck the connection settings (Options → Database), and that the SQL login can create " +
                "the database or its tables.",
                "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// Idempotently creates the <c>FileBlobs</c> table on databases created before it existed.
    /// <c>CreateTables()</c> only runs when a database has no tables, so an already-populated store would
    /// otherwise never get this table. Fresh databases already have it (from <c>CreateTables()</c>); the
    /// guarded DDL is then a no-op.
    /// </summary>
    private static void EnsureFileBlobsTable(ForgeTekDbContext db)
    {
        if (db.Database.IsSqlServer())
        {
            db.Database.ExecuteSqlRaw(
                "IF OBJECT_ID(N'[FileBlobs]', N'U') IS NULL " +
                "CREATE TABLE [FileBlobs] (" +
                "[Sha256] nvarchar(450) NOT NULL CONSTRAINT [PK_FileBlobs] PRIMARY KEY, " +
                "[Length] bigint NOT NULL, " +
                "[Compressed] bit NOT NULL, " +
                "[Content] varbinary(max) NOT NULL, " +
                "[CreatedUtc] datetime2 NOT NULL);");
        }
        else
        {
            db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS \"FileBlobs\" (" +
                "\"Sha256\" TEXT NOT NULL CONSTRAINT \"PK_FileBlobs\" PRIMARY KEY, " +
                "\"Length\" INTEGER NOT NULL, " +
                "\"Compressed\" INTEGER NOT NULL, " +
                "\"Content\" BLOB NOT NULL, " +
                "\"CreatedUtc\" TEXT NOT NULL);");
        }
    }

    /// <summary>Idempotently creates the <c>Certificates</c> table on databases created before it existed.</summary>
    private static void EnsureCertificatesTable(ForgeTekDbContext db)
    {
        if (db.Database.IsSqlServer())
        {
            db.Database.ExecuteSqlRaw(
                "IF OBJECT_ID(N'[Certificates]', N'U') IS NULL " +
                "CREATE TABLE [Certificates] (" +
                "[Id] nvarchar(450) NOT NULL CONSTRAINT [PK_Certificates] PRIMARY KEY, " +
                "[Subject] nvarchar(max) NOT NULL, " +
                "[FriendlyName] nvarchar(max) NOT NULL, " +
                "[Thumbprint] nvarchar(max) NOT NULL, " +
                "[Pfx] varbinary(max) NOT NULL, " +
                "[CreatedUtc] datetime2 NOT NULL, " +
                "[CreatedBy] nvarchar(max) NULL);");
        }
        else
        {
            db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS \"Certificates\" (" +
                "\"Id\" TEXT NOT NULL CONSTRAINT \"PK_Certificates\" PRIMARY KEY, " +
                "\"Subject\" TEXT NOT NULL, " +
                "\"FriendlyName\" TEXT NOT NULL, " +
                "\"Thumbprint\" TEXT NOT NULL, " +
                "\"Pfx\" BLOB NOT NULL, " +
                "\"CreatedUtc\" TEXT NOT NULL, " +
                "\"CreatedBy\" TEXT NULL);");
        }
    }
}
