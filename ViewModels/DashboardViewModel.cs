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
    private MainViewModel _main = null!;

    public ObservableCollection<AppEntryViewModel> Apps { get; } = [];
    public ObservableCollection<ActivityItem> RecentActivity { get; } = [];

    [ObservableProperty] private int _totalApps;
    [ObservableProperty] private int _publishedCount;
    [ObservableProperty] private int _inProgressCount;
    [ObservableProperty] private int _setupCount;
    [ObservableProperty] private bool _hasApps;
    [ObservableProperty] private bool _hasActivity;

    public DashboardViewModel(IStorageService storage, ISetupStorageService setupStorage)
    {
        _storage = storage;
        _setupStorage = setupStorage;
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

        BuildActivity(entries);
    }

    private void BuildActivity(IReadOnlyList<AppEntry> entries)
    {
        var items = new List<ActivityItem>();

        // Setup generations (have real timestamps).
        foreach (var h in _setupStorage.GetHistory())
            items.Add(new ActivityItem("", $"Generated {h.BundleName} setup",
                string.IsNullOrWhiteSpace(h.Version) ? "Setup bundle" : $"v{h.Version}", h.GeneratedDate));

        // Latest version event per app (timed by ScanDate — no publish timestamp is stored).
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
            items.Add(new ActivityItem(glyph, title, v.Status.ToString(), v.ScanDate));
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

    [RelayCommand] private void AddApp()     => _main.AddApp();
    [RelayCommand] private void OpenSetups() => _main.NavigateToSetups();
    [RelayCommand] private void OpenOptions() => _main.NavigateToOptions();
}

/// <summary>One row in the dashboard's recent-activity feed.</summary>
public record ActivityItem(string Glyph, string Title, string Subtitle, DateTime When);
