using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly IStorageService _storage;
    private readonly IDialogService _dialog;
    private bool _suppressNav;

    [ObservableProperty] private object _currentView = new WelcomeViewModel();
    [ObservableProperty] private AppEntryViewModel? _selectedApp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotShowingOptions))]
    [NotifyPropertyChangedFor(nameof(IsNotShowingSetups))]
    [NotifyPropertyChangedFor(nameof(ShowBottomButtons))]
    private bool _isShowingOptions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotShowingSetups))]
    [NotifyPropertyChangedFor(nameof(IsNotShowingOptions))]
    private bool _isShowingSetups;

    public bool IsNotShowingOptions => !IsShowingOptions && !IsShowingSetups;
    public bool IsNotShowingSetups => !IsShowingSetups;
    public bool ShowBottomButtons => !IsShowingOptions;

    public ObservableCollection<AppEntryViewModel> Apps { get; } = [];

    public MainViewModel(IServiceProvider services)
    {
        _services = services;
        _storage = services.GetRequiredService<IStorageService>();
        _dialog = services.GetRequiredService<IDialogService>();
        ReloadApps();
    }

    partial void OnSelectedAppChanged(AppEntryViewModel? value)
    {
        if (_suppressNav || value is null) return;
        var vm = _services.GetRequiredService<AppDetailViewModel>();
        vm.Initialize(value.Entry, this);
        CurrentView = vm;
    }

    public void AddApp()
    {
        var result = _dialog.ShowAddEditApp();
        if (result is null) return;

        var entry = new AppEntry { Name = result.Name, FolderPath = result.Path, InitialVersion = result.InitialVersion, AccentColor = result.AccentColor };
        _storage.Add(entry);
        NavigateToDetail(entry);
    }

    public void NavigateToDetail(AppEntry entry)
    {
        IsShowingOptions = false;
        IsShowingSetups = false;
        _storage.Update(entry);
        var reloaded = _storage.GetById(entry.Id)!;
        var vm = _services.GetRequiredService<AppDetailViewModel>();
        vm.Initialize(reloaded, this);
        SetSelected(entry.Id, vm);
    }

    public void NavigateToScan(AppEntry entry, IReadOnlyList<FileRecord> files, string? detectedVersion = null)
    {
        var vm = _services.GetRequiredService<ScanViewModel>();
        vm.Initialize(entry, files, this, entry.InitialVersion, detectedVersion);
        CurrentView = vm;
    }

    public void NavigateToDiff(AppEntry entry, IReadOnlyList<FileRecord> files, DiffResult diff, string? detectedVersion = null)
    {
        var vm = _services.GetRequiredService<DiffViewModel>();
        vm.Initialize(entry, files, diff, this, detectedVersion: detectedVersion);
        CurrentView = vm;
    }

    public void NavigateToPackage(AppEntry entry, AppVersion version, PackageStep? startFrom = null)
    {
        _storage.Update(entry);
        var vm = _services.GetRequiredService<PackageViewModel>();
        vm.Initialize(entry, version, this, startFrom);
        CurrentView = vm;
    }

    public void NavigateToVersionDiff(AppEntry entry, AppVersion version, DiffResult diff)
    {
        var vm = _services.GetRequiredService<DiffViewModel>();
        vm.Initialize(entry, version.Files, diff, this, isReadOnly: true, viewingVersion: version);
        CurrentView = vm;
    }

    public void NavigateToRevise(AppEntry entry, AppVersion version)
    {
        var vm = _services.GetRequiredService<ReviseViewModel>();
        vm.Initialize(entry, version, this);
        CurrentView = vm;
    }

    public void NavigateToAppSettings(AppEntry entry)
    {
        var vm = _services.GetRequiredService<AppSettingsViewModel>();
        vm.Initialize(entry, this);
        CurrentView = vm;
    }

    partial void OnIsShowingOptionsChanged(bool value)
    {
        if (value) IsShowingSetups = false;
    }

    partial void OnIsShowingSetupsChanged(bool value)
    {
        if (value) IsShowingOptions = false;
    }

    public void NavigateToOptions()
    {
        IsShowingOptions = true;
        var vm = _services.GetRequiredService<GlobalOptionsViewModel>();
        vm.Initialize(this);
        CurrentView = vm;
    }

    public void NavigateToSetups()
    {
        IsShowingSetups = true;
        var vm = _services.GetRequiredService<SetupViewModel>();
        vm.Initialize(this);
        CurrentView = vm;
    }

    public void RefreshSidebar(AppEntry entry)
    {
        var appVm = Apps.FirstOrDefault(a => a.Entry.Id == entry.Id);
        appVm?.SetEntry(entry);
    }

    public void NavigateToWelcome()
    {
        IsShowingOptions = false;
        IsShowingSetups = false;
        ReloadApps();
        _suppressNav = true;
        SelectedApp = null;
        _suppressNav = false;
        CurrentView = new WelcomeViewModel();
    }

    private void SetSelected(string entryId, object? viewModel = null)
    {
        ReloadApps();
        _suppressNav = true;
        SelectedApp = Apps.FirstOrDefault(a => a.Entry.Id == entryId);
        _suppressNav = false;
        if (viewModel is not null)
            CurrentView = viewModel;
    }

    private void ReloadApps()
    {
        Apps.Clear();
        foreach (var app in _storage.GetAll())
            Apps.Add(new AppEntryViewModel(app));
    }
}
