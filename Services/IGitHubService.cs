namespace ForgeTekUpdatePackager.Services;

/// <summary>The latest published GitHub Release of a repo.</summary>
public record GitHubRelease(string TagName, string Name, string HtmlUrl, DateTime? PublishedAt);

public interface IGitHubService
{
    /// <summary>Latest published Release, or null if the repo has none. Throws on auth/network errors.</summary>
    Task<GitHubRelease?> GetLatestReleaseAsync(string repo, string? token, CancellationToken ct = default);

    /// <summary>Human-readable connection result for a "Test connection" button.</summary>
    Task<string> ValidateAsync(string repo, string? token, CancellationToken ct = default);

    /// <summary>Runs "git pull" in <paramref name="localPath"/> then the PowerShell
    /// <paramref name="buildCommand"/> there, streaming output. Throws on a non-zero exit.</summary>
    Task BuildAsync(string localPath, string buildCommand, IProgress<string> progress, CancellationToken ct = default);
}
