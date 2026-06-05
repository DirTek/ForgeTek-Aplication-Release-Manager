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

        var services = new ServiceCollection();

        // Singletons (shared state or expensive to create)
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IStorageService, StorageService>();
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<IScannerService, ScannerService>();
        services.AddSingleton<ISigningService, SigningService>();

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
        services.AddTransient<AppDetailViewModel>();
        services.AddTransient<ScanViewModel>();
        services.AddTransient<DiffViewModel>();
        services.AddTransient<PackageViewModel>();
        services.AddTransient<ReviseViewModel>();
        services.AddTransient<AppSettingsViewModel>();
        services.AddTransient<GlobalOptionsViewModel>();
        services.AddTransient<SetupViewModel>();

        _services = services.BuildServiceProvider();

        var vm = _services.GetRequiredService<MainViewModel>();
        new MainWindow(vm).Show();
    }
}
