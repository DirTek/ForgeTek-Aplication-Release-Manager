using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public interface ISourceCompareService
{
    /// <summary>Compares the file sets + content hashes of up to three folders (the app's tracked files,
    /// its solution build output, and its GitHub build output). Pass null for a source to omit it.</summary>
    Task<ComparisonReport> CompareAsync(string? filesDir, string? slnDir, string? githubDir,
        IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Compares a local working copy against a GitHub repo tree (git blob SHA-1). Files=local,
    /// GitHub=remote; only repo-tracked files are considered.</summary>
    Task<ComparisonReport> CompareWithTreeAsync(string localDir, IReadOnlyList<RepoTreeEntry> tree,
        IProgress<string>? progress = null, CancellationToken ct = default);
}
