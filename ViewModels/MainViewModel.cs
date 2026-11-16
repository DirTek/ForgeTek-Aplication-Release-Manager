using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ForgeTekApplicationReleaseManager.Dialogs;
using ForgeTekApplicationReleaseManager.Models;
using ForgeTekApplicationReleaseManager.Services;

namespace ForgeTekApplicationReleaseManager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly IStorageService _storage;
    private readonly IDialogService _dialog;
    private readonly ILocalizationService _loc;
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
    public string SetupsButtonText => IsShowingSetups
        ? _loc.Get("Str.Main.BackToApps")
        : _loc.Get("Str.Main.Setups");

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
        _loc = services.GetRequiredService<ILocalizationService>();
        Session = services.GetRequiredService<ISessionService>();

        // Re-translate the bound sidebar button text when the language changes.
        _loc.LanguageChanged += (_, _) => OnPropertyChanged(nameof(SetupsButtonText));

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

    /// <summary>Runs while a new version is being approved, BEFORE it is saved: if the app is connected to
    /// GitHub and its changelog has no entry for <paramref name="versionNumber"/>, offers to document the
    /// changes (the release-notes editor writes the changelog). Then re-reads the changelog in the scanned
    /// folder so the saved version records the file as it is now — otherwise a changelog written after the
    /// scan keeps its stale checksum (and an incremental package skips it) or, if it was just created, is
    /// missing from the version entirely. Returns the changelog's record when it changed, else null.</summary>
    public FileRecord? DocumentChangesBeforeSave(AppEntry entry, string versionNumber, List<FileRecord> files)
    {
        var settings = _services.GetRequiredService<ISettingsService>();
        var s = settings.LoadAppSettings(entry.Name);
        if (!string.IsNullOrWhiteSpace(s.GitHubRepo))   // only GitHub-connected apps get the prompt
        {
            var changelog = _services.GetRequiredService<IChangelogService>();
            var path = changelog.FindChangelogFile(entry.FolderPath);
            if ((path is null || !changelog.HasChangelogEntry(path, versionNumber)) &&
                _dialog.Confirm(_loc.Get("Str.MainVm.UpdateChangelogTitle"),
                    _loc.Get("Str.MainVm.UpdateChangelogMsg", versionNumber), _loc.Get("Str.MainVm.AddChanges")))
            {
                OpenReleaseNotes(entry, versionNumber, VersionBefore(entry, versionNumber));
            }
        }
        return RefreshChangelogRecord(entry, files);
    }

    /// <summary>Opens the release-notes editor for <paramref name="entry"/>, prefilled for
    /// <paramref name="versionNumber"/> with the range starting at <paramref name="previous"/>.
    /// Returns true when the editor wrote the changelog.</summary>
    public bool OpenReleaseNotes(AppEntry entry, string? versionNumber, AppVersion? previous)
    {
        var settings = _services.GetRequiredService<ISettingsService>();
        var s = settings.LoadAppSettings(entry.Name);
        if (string.IsNullOrWhiteSpace(s.GitHubRepo)) return false;

        var token = string.IsNullOrWhiteSpace(s.GitHubToken) ? settings.Global.GitHubToken : s.GitHubToken;
        var slnFolder = !string.IsNullOrWhiteSpace(entry.SolutionPath)
            && ProjectLocator.Resolve(entry.SolutionPath) is { } t
            ? System.IO.Path.GetDirectoryName(t) : null;
        var window = new ReleaseNotesWindow(
            _services.GetRequiredService<IGitHubService>(), _services.GetRequiredService<IChangelogService>(),
            s.GitHubRepo!, token, entry.FolderPath, entry.Name, versionNumber, solutionFolder: slnFolder,
            previousVersion: previous?.VersionNumber, previousVersionDate: previous?.ScanDate)
        {
            Owner = Application.Current.MainWindow,
        };
        window.ShowDialog();
        return window.ChangelogWritten;
    }

    /// <summary>The live (not retracted/scrapped) version released before <paramref name="versionNumber"/>.</summary>
    public static AppVersion? VersionBefore(AppEntry entry, string? versionNumber) => entry.Versions
        .Where(v => v.Status != VersionStatus.Retracted && v.Status != VersionStatus.Scrapped)
        .LastOrDefault(v => !string.Equals(v.VersionNumber, versionNumber?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Updates (or adds) the changelog's record in <paramref name="files"/> from the file currently in
    /// the app folder, and stores its content for networked packaging. Returns the record when it changed.</summary>
    public FileRecord? RefreshChangelogRecord(AppEntry entry, List<FileRecord> files)
    {
        var log = _services.GetRequiredService<ILogService>();
        try
        {
            var path = _services.GetRequiredService<IChangelogService>().FindChangelogFile(entry.FolderPath);
            if (path is null) return null;

            var rel = System.IO.Path.GetRelativePath(entry.FolderPath, path);
            var checksum = ScannerService.ComputeChecksum(path);
            var record = files.FirstOrDefault(f => string.Equals(f.Path, rel, StringComparison.OrdinalIgnoreCase));
            if (record is not null && record.Checksum == checksum) return null;

            if (record is null) { record = new FileRecord { Path = rel }; files.Add(record); }
            record.Checksum = checksum;
            record.DateModified = System.IO.File.GetLastWriteTime(path);
            log.Write("Changelog", $"{entry.Name}: {rel} updated after the scan — version now records the new content.");

            // Networked: the scan already stored the old content; store the new one so other machines can package it.
            var blobs = _services.GetRequiredService<Services.Storage.IFileBlobStore>();
            var folder = entry.FolderPath;
            var stored = record;
            _ = Task.Run(async () =>
            {
                try { await blobs.StoreAsync(folder, [stored]); }
                catch (Exception ex) { log.Write("Blobs", $"Storing the updated changelog failed: {ex.Message}"); }
            });
            return record;
        }
        catch (Exception ex)
        {
            log.Write("Changelog", $"{entry.Name}: could not re-read the changelog: {ex.Message}");
            return null;
        }
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
            Apps.Add(new AppEntryViewModel(app, _loc));
    }
}
