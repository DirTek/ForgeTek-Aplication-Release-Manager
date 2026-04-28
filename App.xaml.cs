using System.Windows;
using ForgeTekUpdatePackager.Services;
using ForgeTekUpdatePackager.ViewModels;

namespace ForgeTekUpdatePackager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var storage = new StorageService();
        var scanner = new ScannerService();
        var vm = new MainViewModel(storage, scanner);
        new MainWindow(vm).Show();
    }
}
