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

        var vm = _services.GetRequiredService<MainViewModel>();
        new MainWindow(vm).Show();
    }
}
