using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ForgeTekUpdatePackager.Dialogs;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly StorageService _storage;
    private readonly ScannerService _scanner;
    private readonly SigningService _signing;
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private bool _suppressNav;

    [ObservableProperty] private object _currentView = new WelcomeViewModel();
    [ObservableProperty] private AppEntryViewModel? _selectedApp;

    public ObservableCollection<AppEntryViewModel> Apps { get; } = [];

    public MainViewModel(StorageService storage, ScannerService scanner, SigningService signing, SettingsService settings, LogService log)
    {
        _storage  = storage;
        _scanner  = scanner;
        _signing  = signing;
        _settings = settings;
        _log      = log;
        ReloadApps();
    }

    partial void OnSelectedAppChanged(AppEntryViewModel? value)
    {
        if (_suppressNav || value is null) return;
        CurrentView = new AppDetailViewModel(value.Entry, this, _storage, _scanner, _log);
    }

    public void AddApp()
    {
        var dlg = new AddEditAppDialog { Owner = System.Windows.Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var entry = new AppEntry { Name = dlg.AppName, FolderPath = dlg.AppPath, InitialVersion = dlg.InitialVersion };
        _storage.Add(entry);
        SetSelected(entry.Id, () => new AppDetailViewModel(entry, this, _storage, _scanner, _log));
    }

    public void NavigateToDetail(AppEntry entry)
    {
        _storage.Update(entry);
        var reloaded = _storage.GetById(entry.Id)!;
        SetSelected(entry.Id, () => new AppDetailViewModel(reloaded, this, _storage, _scanner, _log));
    }

    public void NavigateToScan(AppEntry entry, IReadOnlyList<FileRecord> files, string? detectedVersion = null)
        => CurrentView = new ScanViewModel(entry, files, this, _storage, entry.InitialVersion, detectedVersion);

    public void NavigateToDiff(AppEntry entry, IReadOnlyList<FileRecord> files, DiffResult diff, string? detectedVersion = null)
        => CurrentView = new DiffViewModel(entry, files, diff, this, _storage, detectedVersion: detectedVersion);

    public void NavigateToPackage(AppEntry entry, AppVersion version, PackageStep? startFrom = null)
    {
        _storage.Update(entry);
        CurrentView = new PackageViewModel(entry, version, this, _storage, _signing, _scanner, _settings, _log, startFrom);
    }

    public void NavigateToVersionDiff(AppEntry entry, AppVersion version, DiffResult diff)
        => CurrentView = new DiffViewModel(entry, version.Files, diff, this, _storage,
               isReadOnly: true, viewingVersion: version);

    public void NavigateToRevise(AppEntry entry, AppVersion version)
        => CurrentView = new ReviseViewModel(entry, version, this);

    public void NavigateToAppSettings(AppEntry entry)
        => CurrentView = new AppSettingsViewModel(entry, this, _settings);

    public void NavigateToOptions()
        => CurrentView = new GlobalOptionsViewModel(this, _settings);

    public void RefreshSidebar(AppEntry entry)
    {
        var appVm = Apps.FirstOrDefault(a => a.Entry.Id == entry.Id);
        appVm?.SetEntry(entry);
    }

    public void NavigateToWelcome()
    {
        ReloadApps();
        _suppressNav = true;
        SelectedApp = null;
        _suppressNav = false;
        CurrentView = new WelcomeViewModel();
    }

    private void SetSelected(string entryId, Func<object> viewFactory)
    {
        ReloadApps();
        _suppressNav = true;
        SelectedApp = Apps.FirstOrDefault(a => a.Entry.Id == entryId);
        _suppressNav = false;
        CurrentView = viewFactory();
    }

    private void ReloadApps()
    {
        Apps.Clear();
        foreach (var app in _storage.GetAll())
            Apps.Add(new AppEntryViewModel(app));
    }
}
