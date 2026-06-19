using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    // ── Release notes ───────────────────────────────────────────────────────
    public async Task<IReadOnlyList<string>> GetTagsAsync(string repo, string? token, CancellationToken ct = default)
    {
        var (owner, name) = SplitRepo(repo);
        using var req = Authed(HttpMethod.Get, $"repos/{owner}/{name}/tags?per_page=100", token);
        using var resp = await Http.SendAsync(req, ct);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("GitHub rejected the request — check the token and its 'repo' scope.");
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var tags = new List<string>();
        foreach (var el in doc.RootElement.EnumerateArray())
            if (el.TryGetProperty("name", out var n) && n.GetString() is { } tag)
                tags.Add(tag);
        return tags;
    }

    public async Task<IReadOnlyList<string>> GetBranchesAsync(string repo, string? token, CancellationToken ct = default)
    {
        var (owner, name) = SplitRepo(repo);
        using var req = Authed(HttpMethod.Get, $"repos/{owner}/{name}/branches?per_page=100", token);
        using var resp = await Http.SendAsync(req, ct);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("GitHub rejected the request — check the token and its 'repo' scope.");
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var branches = new List<string>();
        foreach (var el in doc.RootElement.EnumerateArray())
            if (el.TryGetProperty("name", out var n) && n.GetString() is { } b)
                branches.Add(b);
        return branches;
    }

    public async Task<IReadOnlyList<CommitChange>> GetCompareChangesAsync(
        string repo, string? token, string fromRef, string toRef, CancellationToken ct = default)
    {
        var (owner, name) = SplitRepo(repo);
        using var req = Authed(HttpMethod.Get,
            $"repos/{owner}/{name}/compare/{Uri.EscapeDataString(fromRef)}...{Uri.EscapeDataString(toRef)}", token);
        using var resp = await Http.SendAsync(req, ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Could not compare {fromRef}...{toRef} — check both refs exist.");
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("GitHub rejected the request — check the token and its 'repo' scope.");
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        return root.TryGetProperty("commits", out var commits) ? ExtractChanges(commits) : [];
    }

    public async Task<IReadOnlyList<CommitChange>> GetRecentChangesAsync(
        string repo, string? token, string branch, int count, CancellationToken ct = default)
    {
        var (owner, name) = SplitRepo(repo);
        var perPage = Math.Clamp(count, 1, 100);
        using var req = Authed(HttpMethod.Get,
            $"repos/{owner}/{name}/commits?sha={Uri.EscapeDataString(branch)}&per_page={perPage}", token);
        using var resp = await Http.SendAsync(req, ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Could not list commits on '{branch}' — check the branch name.");
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("GitHub rejected the request — check the token and its 'repo' scope.");
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return ExtractChanges(doc.RootElement);
    }

    public async Task<RepoCommit?> GetLastCommitAsync(string repo, string? token, CancellationToken ct = default)
    {
        var (owner, name) = SplitRepo(repo);
        using var req = Authed(HttpMethod.Get, $"repos/{owner}/{name}/commits?per_page=1", token);
        using var resp = await Http.SendAsync(req, ct);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("GitHub rejected the request — check the token and its 'repo' scope.");
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var arr = doc.RootElement;
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return null;

        var c = arr[0];
        var commit = c.TryGetProperty("commit", out var cm) ? cm : default;
        var message = commit.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
        var firstLine = message.Split('\n')[0].Trim();
        var author = commit.TryGetProperty("author", out var au) && au.TryGetProperty("name", out var an)
            ? an.GetString() ?? "" : "";
        DateTime? date = commit.TryGetProperty("author", out var ad) && ad.TryGetProperty("date", out var dt)
            && dt.TryGetDateTime(out var parsed) ? parsed.ToLocalTime() : null;

        return new RepoCommit(firstLine, author, date);
    }

    // Summarizes each commit in an array (compare or commits endpoint): first message line,
    // conventional prefix stripped, with a best-guess bucket. Skips noisy merge-commit lines.
    private static List<CommitChange> ExtractChanges(JsonElement commitsArray)
    {
        var changes = new List<CommitChange>();
        foreach (var c in commitsArray.EnumerateArray())
        {
            var message = c.TryGetProperty("commit", out var commit) &&
                          commit.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
            var firstLine = message.Split('\n')[0].Trim();
            if (firstLine.Length == 0) continue;
            if (firstLine.StartsWith("Merge branch", StringComparison.OrdinalIgnoreCase) ||
                firstLine.StartsWith("Merge remote-tracking", StringComparison.OrdinalIgnoreCase))
                continue;

            var (category, text) = Categorize(firstLine);
            var suggested = category switch { "feat" => "Added", "fix" => "Fixed", _ => "" };
            changes.Add(new CommitChange(suggested, text));
        }
        return changes;
    }

    public async Task PublishReleaseNotesAsync(
        string repo, string? token, string tag, string body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Publishing release notes needs a token with write access to the repo.");

        var (owner, name) = SplitRepo(repo);

        // Update the existing release for this tag if there is one, else create it.
        using (var getReq = Authed(HttpMethod.Get, $"repos/{owner}/{name}/releases/tags/{Uri.EscapeDataString(tag)}", token))
        using (var getResp = await Http.SendAsync(getReq, ct))
        {
            if (getResp.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await getResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var id = doc.RootElement.GetProperty("id").GetInt64();
                using var patch = Authed(HttpMethod.Patch, $"repos/{owner}/{name}/releases/{id}", token);
                patch.Content = JsonBody(new { body });
                using var patchResp = await Http.SendAsync(patch, ct);
                patchResp.EnsureSuccessStatusCode();
                return;
            }
        }

        using var post = Authed(HttpMethod.Post, $"repos/{owner}/{name}/releases", token);
        post.Content = JsonBody(new { tag_name = tag, name = tag, body });
        using var postResp = await Http.SendAsync(post, ct);
        if (postResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("GitHub rejected the request — the token needs write ('repo') scope.");
        postResp.EnsureSuccessStatusCode();
    }

    private static (string Category, string Text) Categorize(string firstLine)
    {
        // Conventional-commit prefix → category; strip the prefix for display.
        var m = Regex.Match(firstLine, @"^(?<type>feat|fix|docs|chore|refactor|perf|style|test|build|ci)(\([^)]*\))?!?:\s*(?<rest>.+)$",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var type = m.Groups["type"].Value.ToLowerInvariant();
            var rest = m.Groups["rest"].Value.Trim();
            var category = type switch { "feat" => "feat", "fix" => "fix", _ => "change" };
            return (category, Capitalize(rest));
        }
        return ("change", firstLine);
    }

    private static void AppendSection(StringBuilder sb, string header, List<string> items)
    {
        if (items.Count == 0) return;
        sb.AppendLine($"### {header}");
        foreach (var item in items)
            sb.AppendLine($"- {item}");
        sb.AppendLine();
    }

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static HttpRequestMessage Authed(HttpMethod method, string url, string? token)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        return req;
    }

    private static StringContent JsonBody(object payload)
        => new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

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
