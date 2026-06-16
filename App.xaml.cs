using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using ForgeTekUpdatePackager.Services;
using ForgeTekUpdatePackager.ViewModels;

namespace ForgeTekUpdatePackager;

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

        // Singletons (shared state or expensive to create)
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IStorageService, StorageService>();
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<IScannerService, ScannerService>();
        services.AddSingleton<ISigningService, SigningService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IUserService, UserService>();
        services.AddSingleton<ISessionService, SessionService>();
        services.AddSingleton<IProtectionStateService, ProtectionStateService>();
        services.AddSingleton<IGitHubService, GitHubService>();
        services.AddSingleton<IGitHubAuthService, GitHubAuthService>();

        // Dialog service (created per-need, wraps WPF dialogs)
        services.AddSingleton<IDialogService, DialogService>();

        // Transient (stateless, created per-need)
        services.AddTransient<IPackagingService, PackagingService>();
        services.AddTransient<IFtpService, FtpService>();
        services.AddTransient<IManifestService, ManifestService>();
        services.AddTransient<IUpdateCatalogService, UpdateCatalogService>();
        services.AddTransient<ICertificateService, CertificateService>();
        services.AddTransient<IBackupService, BackupService>();
        services.AddTransient<IChangelogService, ChangelogService>();

        // Setup services
        services.AddSingleton<ISetupStorageService, SetupStorageService>();
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
                var lockout = new Dialogs.LockoutWindow();
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
}
