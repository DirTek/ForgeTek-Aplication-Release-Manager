using System.Collections.Concurrent;

namespace ForgeTekApplicationReleaseManager.Services;

public class ConnectionStatusCache : IConnectionStatusCache
{
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string appName) => _cache.TryGetValue(appName, out var state) ? state : null;

    public void Set(string appName, string state) => _cache[appName] = state;

    public void Invalidate(string appName) => _cache.TryRemove(appName, out _);
}
