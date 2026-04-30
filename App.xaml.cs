using System.Windows;
using ForgeTekUpdatePackager.Services;
using ForgeTekUpdatePackager.ViewModels;

namespace ForgeTekUpdatePackager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var settings = new SettingsService();
        var storage  = new StorageService(settings);
        var scanner  = new ScannerService();
        var signing  = new SigningService();
        var log      = new LogService(settings);
        var vm       = new MainViewModel(storage, scanner, signing, settings, log);
        new MainWindow(vm).Show();
    }
}
