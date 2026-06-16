using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ForgeTekUpdatePackager.Services;

/// <summary>
/// Minimal GitHub REST client (releases). Reuses a single <see cref="HttpClient"/>. A personal access
/// token is optional for public repos; when supplied it's sent as a Bearer token.
/// </summary>
public class GitHubService : IGitHubService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ForgeTek-Release-Manager");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public async Task<GitHubRelease?> GetLatestReleaseAsync(string repo, string? token, CancellationToken ct = default)
    {
        var (owner, name) = SplitRepo(repo);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{owner}/{name}/releases/latest");
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

        using var resp = await Http.SendAsync(req, ct);

        // "latest" 404s when the repo has no published releases — that's not an error.
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("GitHub rejected the request — check the token and its 'repo' scope.");
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var tag  = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        var rel  = root.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        var url  = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
        DateTime? published = root.TryGetProperty("published_at", out var p) && p.TryGetDateTime(out var dt) ? dt : null;

        return new GitHubRelease(tag, string.IsNullOrWhiteSpace(rel) ? tag : rel, url, published);
    }

    public async Task<string> ValidateAsync(string repo, string? token, CancellationToken ct = default)
    {
        try
        {
            var release = await GetLatestReleaseAsync(repo, token, ct);
            return release is null
                ? "✓ Repository reachable — no published releases yet."
                : $"✓ Connected — latest release {release.TagName}.";
        }
        catch (Exception ex)
        {
            return $"✗ {ex.Message}";
        }
    }

    // ── Build runner ──────────────────────────────────────────────────────
    public async Task BuildAsync(string localPath, string buildCommand, IProgress<string> progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !System.IO.Directory.Exists(localPath))
            throw new InvalidOperationException($"Local repo path not found:\n{localPath}");
        if (string.IsNullOrWhiteSpace(buildCommand))
            throw new InvalidOperationException("No build command configured.");

        progress.Report($"> git pull   (in {localPath})");
        await RunAsync("git", "pull", localPath, progress, ct);

        progress.Report(string.Empty);
        progress.Report($"> {buildCommand}");
        // -NoProfile keeps it fast/clean; -Command runs the user's build line in the repo folder.
        await RunAsync("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -Command \"{buildCommand.Replace("\"", "\\\"")}\"",
            localPath, progress, ct);
    }

    private static async Task RunAsync(string fileName, string arguments, string workingDir,
        IProgress<string> progress, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = fileName,
            Arguments              = arguments,
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");

        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) progress.Report(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) progress.Report(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} exited with code {proc.ExitCode}.");
    }

    private static (string Owner, string Name) SplitRepo(string repo)
    {
        var parts = (repo ?? string.Empty).Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new InvalidOperationException("Repository must be in \"owner/name\" form.");
        return (parts[^2], parts[^1]);
    }

    // ── Version comparison ────────────────────────────────────────────────
    /// <summary>Compares dotted numeric versions ("v0.1.40" vs "0.1.37"); returns -1/0/1.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        var pa = Parse(a);
        var pb = Parse(b);
        var len = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < len; i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }

    public static bool IsNewer(string? candidate, string? baseline) => CompareVersions(candidate, baseline) > 0;

    private static int[] Parse(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return [];
        v = v.Trim();
        if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = v[1..];
        // Drop any pre-release/build suffix, keep the leading dotted-number part.
        var main = v.Split('-', ' ', '+')[0];
        return main.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
    }
}
