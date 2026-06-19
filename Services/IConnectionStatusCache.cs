namespace ForgeTekUpdatePackager.Services;

/// <summary>
/// Session-lived cache of each app's last publish-target connection result, so the dashboard
/// doesn't re-test the network every time it's shown. Invalidated when an app's settings change.
/// </summary>
public interface IConnectionStatusCache
{
    /// <summary>Returns a cached "Online"/"Offline" state for the app, or null if not checked yet.</summary>
    string? Get(string appName);

    void Set(string appName, string state);

    /// <summary>Drops the cached result for an app (e.g. after its settings were saved).</summary>
    void Invalidate(string appName);
}
