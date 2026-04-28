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
    private bool _suppressNav;

    [ObservableProperty] private object _currentView = new WelcomeViewModel();
    [ObservableProperty] private AppEntryViewModel? _selectedApp;

    public ObservableCollection<AppEntryViewModel> Apps { get; } = [];

    public MainViewModel(StorageService storage, ScannerService scanner)
    {
        _storage = storage;
        _scanner = scanner;
        ReloadApps();
    }

    partial void OnSelectedAppChanged(AppEntryViewModel? value)
    {
        if (_suppressNav || value is null) return;
        CurrentView = new AppDetailViewModel(value.Entry, this, _storage, _scanner);
    }

    public void AddApp()
    {
        var dlg = new AddEditAppDialog { Owner = System.Windows.Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var entry = new AppEntry { Name = dlg.AppName, FolderPath = dlg.AppPath, InitialVersion = dlg.InitialVersion };
        _storage.Add(entry);
        SetSelected(entry.Id, () => new AppDetailViewModel(entry, this, _storage, _scanner));
    }

    public void NavigateToDetail(AppEntry entry)
    {
        _storage.Update(entry);
        var reloaded = _storage.GetById(entry.Id)!;
        SetSelected(entry.Id, () => new AppDetailViewModel(reloaded, this, _storage, _scanner));
    }

    public void NavigateToScan(AppEntry entry, IReadOnlyList<FileRecord> files)
        => CurrentView = new ScanViewModel(entry, files, this, _storage, entry.InitialVersion);

    public void NavigateToDiff(AppEntry entry, IReadOnlyList<FileRecord> files, DiffResult diff)
        => CurrentView = new DiffViewModel(entry, files, diff, this, _storage);

    public void NavigateToVersionDiff(AppEntry entry, AppVersion version, DiffResult diff)
        => CurrentView = new DiffViewModel(entry, version.Files, diff, this, _storage,
               isReadOnly: true, viewingVersion: version);

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
