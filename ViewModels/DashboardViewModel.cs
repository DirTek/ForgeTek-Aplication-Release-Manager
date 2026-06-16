using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.ViewModels;

/// <summary>Home screen: an at-a-glance overview of every app's release state, summary counts,
/// recent activity, and quick actions. Shown whenever no app is selected.</summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly IStorageService _storage;
    private readonly ISetupStorageService _setupStorage;
    private readonly ISettingsService _settings;
    private MainViewModel _main = null!;

    public ISessionService Session { get; }

    public ObservableCollection<AppEntryViewModel> Apps { get; } = [];
    public ObservableCollection<ActivityItem> RecentActivity { get; } = [];

    [ObservableProperty] private int _totalApps;
    [ObservableProperty] private int _publishedCount;
    [ObservableProperty] private int _inProgressCount;
    [ObservableProperty] private int _setupCount;
    [ObservableProperty] private bool _hasApps;
    [ObservableProperty] private bool _hasActivity;

    [ObservableProperty] private bool _gitHubConnected;
    [ObservableProperty] private string _gitHubStatusText = "Not connected";

    public DashboardViewModel(IStorageService storage, ISetupStorageService setupStorage,
        ISessionService session, ISettingsService settings)
    {
        _storage = storage;
        _setupStorage = setupStorage;
        Session = session;
        _settings = settings;
    }

    public void Initialize(MainViewModel main)
    {
        _main = main;

        var entries = _storage.GetAll();
        Apps.Clear();
        foreach (var e in entries)
            Apps.Add(new AppEntryViewModel(e));

        TotalApps       = Apps.Count;
        HasApps         = Apps.Count > 0;
        PublishedCount  = Apps.Count(a => a.DisplayMode == VersionDisplayMode.Published);
        InProgressCount = Apps.Count(a => a.DisplayMode == VersionDisplayMode.InProgress);
        SetupCount      = _setupStorage.GetAll().Count;

        GitHubConnected = !string.IsNullOrWhiteSpace(_settings.Global.GitHubToken);
        GitHubStatusText = GitHubConnected
            ? (string.IsNullOrWhiteSpace(_settings.Global.GitHubLogin) ? "GitHub connected" : $"GitHub: @{_settings.Global.GitHubLogin}")
            : "GitHub: not connected";

        BuildActivity(entries);
    }

    private void BuildActivity(IReadOnlyList<AppEntry> entries)
    {
        var items = new List<ActivityItem>();

        // Setup generations (have real timestamps).
        foreach (var h in _setupStorage.GetHistory())
            items.Add(new ActivityItem("", $"Generated {h.BundleName} setup",
                string.IsNullOrWhiteSpace(h.Version) ? "Setup bundle" : $"v{h.Version}", h.GeneratedDate,
                IsSetup: true));

        // Latest version event per app. Published versions carry a real PublishedDate;
        // everything else falls back to the scan timestamp.
        foreach (var e in entries)
        {
            var v = e.Versions.LastOrDefault();
            if (v is null) continue;
            var (glyph, title) = v.Status switch
            {
                VersionStatus.Published => ("", $"Published {e.Name} v{v.VersionNumber}"),
                VersionStatus.Retracted => ("", $"Retracted {e.Name} v{v.VersionNumber}"),
                VersionStatus.Scrapped  => ("", $"Scrapped {e.Name} v{v.VersionNumber}"),
                _                       => ("", $"Scanned {e.Name} v{v.VersionNumber}"),
            };
            var when = v.Status == VersionStatus.Published ? (v.PublishedDate ?? v.ScanDate) : v.ScanDate;
            items.Add(new ActivityItem(glyph, title, v.Status.ToString(), when, AppId: e.Id));
        }

        RecentActivity.Clear();
        foreach (var it in items.OrderByDescending(i => i.When).Take(8))
            RecentActivity.Add(it);
        HasActivity = RecentActivity.Count > 0;
    }

    [RelayCommand]
    private void OpenApp(AppEntryViewModel? app)
    {
        if (app is not null) _main.NavigateToDetail(app.Entry);
    }

    [RelayCommand]
    private void OpenActivity(ActivityItem? item)
    {
        if (item is null) return;
        if (item.IsSetup) { _main.NavigateToSetups(); return; }
        if (item.AppId is not null && _storage.GetById(item.AppId) is { } entry)
            _main.NavigateToDetail(entry);
    }

    [RelayCommand] private void AddApp()     => _main.AddApp();
    [RelayCommand] private void OpenSetups() => _main.NavigateToSetups();
    [RelayCommand] private void OpenOptions() => _main.NavigateToOptions();
}

/// <summary>One row in the dashboard's recent-activity feed. <paramref name="AppId"/> opens that app;
/// <paramref name="IsSetup"/> rows open the Setups screen.</summary>
public record ActivityItem(string Glyph, string Title, string Subtitle, DateTime When,
    string? AppId = null, bool IsSetup = false);
