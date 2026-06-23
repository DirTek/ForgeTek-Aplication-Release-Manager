using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekApplicationReleaseManager.Models;
using ForgeTekApplicationReleaseManager.Services;
using ForgeTekApplicationReleaseManager.Services.Publishing;

namespace ForgeTekApplicationReleaseManager.ViewModels;

/// <summary>Home screen: an at-a-glance overview of every app's release state, summary counts,
/// recent activity, and quick actions. Shown whenever no app is selected.</summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly IStorageService _storage;
    private readonly ISetupStorageService _setupStorage;
    private readonly ISettingsService _settings;
    private readonly IPublishService _publish;
    private readonly IConnectionStatusCache _connCache;
    private readonly ILocalizationService _loc;
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
        ISessionService session, ISettingsService settings, IPublishService publish,
        IConnectionStatusCache connCache, ILocalizationService loc)
    {
        _storage = storage;
        _setupStorage = setupStorage;
        Session = session;
        _settings = settings;
        _publish = publish;
        _connCache = connCache;
        _loc = loc;
    }

    // Tests each app's configured publish target on load and flags the card online/offline.
    // Fire-and-forget: cards show "checking…" then flip as each result arrives. All network work
    // runs on the thread pool (never the UI thread); results are marshalled back via the dispatcher.
    private void CheckConnections()
    {
        // Gather targets on the UI thread (cheap file reads) and set the initial "checking" state.
        var targets = new List<(AppEntryViewModel App, AppSettings Settings)>();
        foreach (var app in Apps)
        {
            var s = _settings.LoadAppSettings(app.Entry.Name);
            if (!_publish.IsConfigured(s))
            {
                app.ConnectionState = "None";
                continue;
            }

            app.ConnectionProvider = _publish.ProviderName(s);

            // Reuse this session's last result instead of re-testing the network on every visit.
            var cached = _connCache.Get(app.Entry.Name);
            if (cached is not null)
            {
                app.ConnectionState = cached;
                continue;
            }

            app.ConnectionState = "Checking";
            targets.Add((app, s));
        }
        if (targets.Count == 0) return;

        // Capture the UI sync context (DispatcherSynchronizationContext) to post results back —
        // avoids referencing WPF's Application/Dispatcher types directly.
        var ui = SynchronizationContext.Current;

        // Off the UI thread: each check is independent and bounded by its own timeout.
        _ = Task.Run(() => Task.WhenAll(targets.Select(async t =>
        {
            string state;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var result = await _publish.TestAsync(t.Settings, cts.Token).ConfigureAwait(false);
                // Every transport reports failure as a leading "✗"; anything else is a success.
                state = !string.IsNullOrWhiteSpace(result) && result.TrimStart().StartsWith('✗')
                    ? "Offline" : "Online";
            }
            catch { state = "Offline"; }

            _connCache.Set(t.App.Entry.Name, state);

            var app = t.App;
            if (ui is not null)
                ui.Post(_ => app.ConnectionState = state, null);
            else
                app.ConnectionState = state;
        })));
    }

    public void Initialize(MainViewModel main)
    {
        _main = main;

        var entries = _storage.GetAll();
        Apps.Clear();
        foreach (var e in entries)
            Apps.Add(new AppEntryViewModel(e, _loc));

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

        CheckConnections();   // background; cards update as results arrive
    }

    /// <summary>Clears cached publish-target statuses and re-tests every app's connection.</summary>
    [RelayCommand]
    private void RecheckConnections()
    {
        foreach (var app in Apps)
            _connCache.Invalidate(app.Entry.Name);
        CheckConnections();
    }

    private void BuildActivity(IReadOnlyList<AppEntry> entries)
    {
        var items = new List<ActivityItem>();

        // Setup generations (have real timestamps).
        foreach (var h in _setupStorage.GetHistory())
            items.Add(new ActivityItem("", $"Generated {h.BundleName} setup",
                $"{(string.IsNullOrWhiteSpace(h.Version) ? "Setup bundle" : $"v{h.Version}")} · by {(string.IsNullOrWhiteSpace(h.GeneratedBy) ? Session.ActorName : h.GeneratedBy)}",
                h.GeneratedDate, IsSetup: true));

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
            var who = !string.IsNullOrWhiteSpace(v.PublishedBy) ? v.PublishedBy! : Session.ActorName;
            items.Add(new ActivityItem(glyph, title, $"{v.Status} · by {who}", when, AppId: e.Id));
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

    /// <summary>Card call-to-action: open the app and immediately run its next step (scan/review/…).</summary>
    [RelayCommand]
    private void RunAppAction(AppEntryViewModel? app)
    {
        if (app is not null) _main.NavigateToDetail(app.Entry, runPrimaryAction: true);
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
