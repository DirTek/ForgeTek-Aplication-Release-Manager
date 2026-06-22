using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ForgeTekUpdatePackager.Dialogs;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly IStorageService _storage;
    private readonly IDialogService _dialog;
    private bool _suppressNav;

    /// <summary>Signed-in user + role permissions, for binding sidebar/command gating.</summary>
    public ISessionService Session { get; }

    [ObservableProperty] private object _currentView = null!;
    [ObservableProperty] private AppEntryViewModel? _selectedApp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotShowingOptions))]
    [NotifyPropertyChangedFor(nameof(IsNotShowingSetups))]
    [NotifyPropertyChangedFor(nameof(ShowBottomButtons))]
    private bool _isShowingOptions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotShowingSetups))]
    [NotifyPropertyChangedFor(nameof(IsNotShowingOptions))]
    [NotifyPropertyChangedFor(nameof(SetupsButtonText))]
    private bool _isShowingSetups;

    public bool IsNotShowingOptions => !IsShowingOptions && !IsShowingSetups;
    public bool IsNotShowingSetups => !IsShowingSetups;
    public bool ShowBottomButtons => !IsShowingOptions;

    /// <summary>Sidebar Setups button doubles as the way back to apps while you're in Setups.</summary>
    public string SetupsButtonText => IsShowingSetups ? "← Back to apps" : "Setups";

    /// <summary>Enter Setups, or (when already there) return to the apps view.</summary>
    public void ToggleSetups()
    {
        if (IsShowingSetups)
        {
            if (SelectedApp is not null) NavigateToDetail(SelectedApp.Entry);
            else NavigateToWelcome();
        }
        else NavigateToSetups();
    }

    public ObservableCollection<AppEntryViewModel> Apps { get; } = [];

    public MainViewModel(IServiceProvider services)
    {
        _services = services;
        _storage = services.GetRequiredService<IStorageService>();
        _dialog = services.GetRequiredService<IDialogService>();
        Session = services.GetRequiredService<ISessionService>();
        NavigateToWelcome();
    }

    public bool IsProtected => Session.IsProtected;
    public string CurrentUserName => Session.Current?.Username ?? string.Empty;

    /// <summary>Sign out and return to the login screen (restarts the shell cleanly).</summary>
    public void SignOut()
    {
        if (!Session.IsProtected) return;
        var exe = Environment.ProcessPath;
        if (exe is not null) System.Diagnostics.Process.Start(exe);
        System.Windows.Application.Current.Shutdown();
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

        var entry = new AppEntry { Name = result.Name, FolderPath = result.Path, InitialVersion = result.InitialVersion, AccentColor = result.AccentColor, SolutionPath = result.SolutionPath };
        _storage.Add(entry);
        NavigateToDetail(entry);
    }

    public void NavigateToDetail(AppEntry entry, bool runPrimaryAction = false)
    {
        IsShowingOptions = false;
        IsShowingSetups = false;
        _storage.Update(entry);
        var reloaded = _storage.GetById(entry.Id)!;
        var vm = _services.GetRequiredService<AppDetailViewModel>();
        vm.Initialize(reloaded, this);
        SetSelected(entry.Id, vm);

        // From the dashboard card's call-to-action: kick off the app's next step (scan/review/
        // package/publish) rather than just landing on the page.
        if (runPrimaryAction && vm.PrimaryActionCommand.CanExecute(null))
            vm.PrimaryActionCommand.Execute(null);
    }

    /// <summary>After a scan, if the app is connected to GitHub and its changelog has no entry for
    /// <paramref name="versionNumber"/>, offer to document the changes (opens the release-notes editor,
    /// which can prepend the new section to the changelog).</summary>
    public void PromptChangelogIfNeeded(AppEntry entry, string versionNumber)
    {
        var settings = _services.GetRequiredService<ISettingsService>();
        var s = settings.LoadAppSettings(entry.Name);
        if (string.IsNullOrWhiteSpace(s.GitHubRepo)) return;   // only for GitHub-connected apps

        var changelog = _services.GetRequiredService<IChangelogService>();
        var path = changelog.FindChangelogFile(entry.FolderPath);
        if (path is not null && changelog.HasChangelogEntry(path, versionNumber))
            return;   // already documented

        if (!_dialog.Confirm("Update Changelog?",
                $"v{versionNumber} isn't in the changelog yet.\n\nAdd its changes now?", "Add Changes"))
            return;

        var token = string.IsNullOrWhiteSpace(s.GitHubToken) ? settings.Global.GitHubToken : s.GitHubToken;
        var slnFolder = !string.IsNullOrWhiteSpace(entry.SolutionPath)
            && ProjectLocator.Resolve(entry.SolutionPath) is { } t
            ? System.IO.Path.GetDirectoryName(t) : null;
        new ReleaseNotesWindow(
            _services.GetRequiredService<IGitHubService>(), changelog,
            s.GitHubRepo!, token, entry.FolderPath, entry.Name, versionNumber, solutionFolder: slnFolder)
        {
            Owner = Application.Current.MainWindow,
        }.ShowDialog();
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
        if (!Session.CanManageSetups) return;   // role gate (defense in depth)
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

    /// <summary>Deselect the current app and return to the Welcome screen.</summary>
    public void DeselectApp() => NavigateToWelcome();

    public void NavigateToWelcome()
    {
        IsShowingOptions = false;
        IsShowingSetups = false;
        ReloadApps();
        _suppressNav = true;
        SelectedApp = null;
        _suppressNav = false;

        var vm = _services.GetRequiredService<DashboardViewModel>();
        vm.Initialize(this);
        CurrentView = vm;
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
