using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ForgeTekUpdatePackager.Services.Publishing;

/// <summary>
/// Publishes to GitHub Releases. Packages attach to a per-version release (tag pattern, default
/// "v{version}"); the update catalog attaches to a single fixed release (default tag "updates") so
/// its download URL stays stable for the updater. Remote path is encoded as "{tag}/{fileName}".
/// </summary>
internal sealed class GitHubReleaseTransport : IFileTransport
{
    private static readonly HttpClient Http = CreateClient();

    private readonly string _owner;
    private readonly string _name;
    private readonly string _token;
    private readonly string _releaseTagPattern;
    private readonly string _catalogTag;

    public GitHubReleaseTransport(string repo, string? token, string? releaseTagPattern, string? catalogTag)
    {
        (_owner, _name) = SplitRepo(repo);
        _token = token?.Trim() ?? string.Empty;
        _releaseTagPattern = string.IsNullOrWhiteSpace(releaseTagPattern) ? "v{version}" : releaseTagPattern.Trim();
        _catalogTag = string.IsNullOrWhiteSpace(catalogTag) ? "updates" : catalogTag.Trim();
    }

    public string DisplayName => "GitHub Releases";

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ForgeTek-Release-Manager");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    private HttpRequestMessage Req(HttpMethod method, string url)
    {
        var r = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(_token))
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return r;
    }

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.SendAsync(Req(HttpMethod.Get, $"https://api.github.com/repos/{_owner}/{_name}"), ct);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return "✗ GitHub rejected the request — check the token and its 'repo' scope.";
            resp.EnsureSuccessStatusCode();
            return $"✓ Connected to {_owner}/{_name}.";
        }
        catch (Exception ex) { return $"✗ {ex.Message}"; }
    }

    // remotePath = "{tag}/{fileName}"
    private static (string Tag, string File) Split(string remotePath)
    {
        var idx = remotePath.IndexOf('/');
        return idx < 0 ? (remotePath, remotePath) : (remotePath[..idx], remotePath[(idx + 1)..]);
    }

    public async Task UploadFileAsync(string localPath, string remotePath, IProgress<string> progress,
        CancellationToken ct = default, IProgress<long>? bytesProgress = null)
    {
        var (tag, file) = Split(remotePath);
        var releaseId = await EnsureReleaseAsync(tag, ct);
        await DeleteAssetIfExistsAsync(releaseId, file, ct);

        progress.Report($"  ↑ {file} → release {tag}");
        var fs = File.OpenRead(localPath);
        var total = fs.Length;
        long lastPct = -1;
        void OnSent(long sent)
        {
            bytesProgress?.Report(sent);
            if (total <= 0) return;
            var pct = sent * 100 / total;
            if (pct >= lastPct + 5) { lastPct = pct; progress.Report($"    {pct}%  ({Human(sent)} / {Human(total)})"); }
        }

        // ProgressableStreamContent disposes the file stream.
        using var content = new ProgressableStreamContent(fs, total, "application/octet-stream", OnSent);
        using var req = Req(HttpMethod.Post,
            $"https://uploads.github.com/repos/{_owner}/{_name}/releases/{releaseId}/assets?name={Uri.EscapeDataString(file)}");
        req.Content = content;
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    private static string Human(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.#} {units[u]}";
    }

    public async Task UploadTextAsync(string content, string remotePath, CancellationToken ct = default)
    {
        var (tag, file) = Split(remotePath);
        var releaseId = await EnsureReleaseAsync(tag, ct);
        await DeleteAssetIfExistsAsync(releaseId, file, ct);

        using var body = new StringContent(content, Encoding.UTF8);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var req = Req(HttpMethod.Post,
            $"https://uploads.github.com/repos/{_owner}/{_name}/releases/{releaseId}/assets?name={Uri.EscapeDataString(file)}");
        req.Content = body;
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<string?> TryDownloadTextAsync(string remotePath, CancellationToken ct = default)
    {
        var (tag, file) = Split(remotePath);
        var release = await GetReleaseAsync(tag, ct);
        if (release is null) return null;

        var assetId = FindAssetId(release.Value, file);
        if (assetId is null) return null;

        using var req = Req(HttpMethod.Get, $"https://api.github.com/repos/{_owner}/{_name}/releases/assets/{assetId}");
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("application/octet-stream");
        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task DeleteFileAsync(string remotePath, CancellationToken ct = default)
    {
        var (tag, file) = Split(remotePath);
        var release = await GetReleaseAsync(tag, ct);
        if (release is null) return;
        var assetId = FindAssetId(release.Value, file);
        if (assetId is null) return;
        using var resp = await Http.SendAsync(
            Req(HttpMethod.Delete, $"https://api.github.com/repos/{_owner}/{_name}/releases/assets/{assetId}"), ct);
    }

    // Deletes the per-version release (remoteDir = the tag) and its git tag ref.
    public async Task DeleteDirAsync(string remoteDir, CancellationToken ct = default)
    {
        var tag = remoteDir.Trim('/');
        var release = await GetReleaseAsync(tag, ct);
        if (release is not null && release.Value.TryGetProperty("id", out var id))
        {
            using var _ = await Http.SendAsync(
                Req(HttpMethod.Delete, $"https://api.github.com/repos/{_owner}/{_name}/releases/{id.GetInt64()}"), ct);
        }
        // Best-effort: remove the dangling tag ref so the tag list stays clean.
        try
        {
            using var __ = await Http.SendAsync(
                Req(HttpMethod.Delete, $"https://api.github.com/repos/{_owner}/{_name}/git/refs/tags/{tag}"), ct);
        }
        catch { }
    }

    public string RemotePath(string appKey, string? version, string fileName)
    {
        var tag = version is null ? _catalogTag : _releaseTagPattern.Replace("{version}", version);
        return $"{tag}/{fileName}";
    }

    public string DownloadUrl(string appKey, string version, string fileName)
    {
        var tag = _releaseTagPattern.Replace("{version}", version);
        return $"https://github.com/{_owner}/{_name}/releases/download/{tag}/{Uri.EscapeDataString(fileName)}";
    }

    // ── GitHub REST helpers ─────────────────────────────────────────────────
    private async Task<long> EnsureReleaseAsync(string tag, CancellationToken ct)
    {
        var existing = await GetReleaseAsync(tag, ct);
        if (existing is not null && existing.Value.TryGetProperty("id", out var id))
            return id.GetInt64();

        var payload = JsonSerializer.Serialize(new { tag_name = tag, name = tag });
        using var req = Req(HttpMethod.Post, $"https://api.github.com/repos/{_owner}/{_name}/releases");
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("id").GetInt64();
    }

    private async Task<JsonElement?> GetReleaseAsync(string tag, CancellationToken ct)
    {
        using var resp = await Http.SendAsync(
            Req(HttpMethod.Get, $"https://api.github.com/repos/{_owner}/{_name}/releases/tags/{tag}"), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    private async Task DeleteAssetIfExistsAsync(long releaseId, string fileName, CancellationToken ct)
    {
        using var resp = await Http.SendAsync(
            Req(HttpMethod.Get, $"https://api.github.com/repos/{_owner}/{_name}/releases/{releaseId}/assets"), ct);
        if (!resp.IsSuccessStatusCode) return;
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        foreach (var asset in doc.RootElement.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var n) &&
                string.Equals(n.GetString(), fileName, StringComparison.OrdinalIgnoreCase) &&
                asset.TryGetProperty("id", out var id))
            {
                using var _ = await Http.SendAsync(
                    Req(HttpMethod.Delete, $"https://api.github.com/repos/{_owner}/{_name}/releases/assets/{id.GetInt64()}"), ct);
                return;
            }
        }
    }

    private static long? FindAssetId(JsonElement release, string fileName)
    {
        if (!release.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var n) &&
                string.Equals(n.GetString(), fileName, StringComparison.OrdinalIgnoreCase) &&
                asset.TryGetProperty("id", out var id))
                return id.GetInt64();
        }
        return null;
    }

    private static (string Owner, string Name) SplitRepo(string repo)
    {
        var parts = (repo ?? string.Empty).Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new InvalidOperationException("GitHub repository must be in \"owner/name\" form.");
        return (parts[^2], parts[^1]);
    }
}
