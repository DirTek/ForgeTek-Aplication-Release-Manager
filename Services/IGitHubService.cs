namespace ForgeTekUpdatePackager.Services;

/// <summary>The latest published GitHub Release of a repo.</summary>
public record GitHubRelease(string TagName, string Name, string HtmlUrl, DateTime? PublishedAt);

/// <summary>One commit summarized for the release-notes categorizer. <paramref name="Suggested"/> is
/// a best-guess bucket ("Added"/"Fixed") from a conventional-commit prefix, or "" when unknown.</summary>
public record CommitChange(string Suggested, string Text);

/// <summary>A recent repo commit for display (message first line + author + date).</summary>
public record RepoCommit(string Message, string Author, DateTime? Date)
{
    public string Meta => Date is { } d ? $"{Author} · {d:yyyy-MM-dd}" : Author;
}

/// <summary>One tracked file in a GitHub repo tree. <paramref name="Sha"/> is the git blob SHA-1.</summary>
public record RepoTreeEntry(string Path, string Sha);

public interface IGitHubService
{
    /// <summary>Latest published Release, or null if the repo has none. Throws on auth/network errors.</summary>
    Task<GitHubRelease?> GetLatestReleaseAsync(string repo, string? token, CancellationToken ct = default);

    /// <summary>Human-readable connection result for a "Test connection" button.</summary>
    Task<string> ValidateAsync(string repo, string? token, CancellationToken ct = default);

    /// <summary>Runs "git pull" in <paramref name="localPath"/> then the PowerShell
    /// <paramref name="buildCommand"/> there, streaming output. Throws on a non-zero exit.</summary>
    Task BuildAsync(string localPath, string buildCommand, IProgress<string> progress, CancellationToken ct = default);

    /// <summary>Tag names in the repo, newest first (up to 100). Throws on auth/network errors.</summary>
    Task<IReadOnlyList<string>> GetTagsAsync(string repo, string? token, CancellationToken ct = default);

    /// <summary>Branch names in the repo (up to 100). Throws on auth/network errors.</summary>
    Task<IReadOnlyList<string>> GetBranchesAsync(string repo, string? token, CancellationToken ct = default);

    /// <summary>Commit summaries between two refs (compare API), for the release-notes categorizer.
    /// Throws on auth/network errors or unknown refs.</summary>
    Task<IReadOnlyList<CommitChange>> GetCompareChangesAsync(string repo, string? token, string fromRef, string toRef, CancellationToken ct = default);

    /// <summary>Commit summaries from the latest <paramref name="count"/> commits on a branch (or any
    /// ref) — for repos that don't use tags. Throws on auth/network errors.</summary>
    Task<IReadOnlyList<CommitChange>> GetRecentChangesAsync(string repo, string? token, string branch, int count, CancellationToken ct = default);

    /// <summary>The most recent commit on the repo's default branch (message + author + date), or null
    /// when the repo has no commits. Throws on auth/network errors.</summary>
    Task<RepoCommit?> GetLastCommitAsync(string repo, string? token, CancellationToken ct = default);

    /// <summary>All tracked files (blobs) in the repo at <paramref name="branch"/>, with their git blob
    /// SHA-1, for comparing a local working copy against the remote. Throws on auth/network errors.</summary>
    Task<IReadOnlyList<RepoTreeEntry>> GetRepoTreeAsync(string repo, string? token, string branch, CancellationToken ct = default);

    /// <summary>Creates a Release for <paramref name="tag"/> with <paramref name="body"/>, or updates
    /// the body if a release for that tag already exists. Requires a token with write access.</summary>
    Task PublishReleaseNotesAsync(string repo, string? token, string tag, string body, CancellationToken ct = default);
}
