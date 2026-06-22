namespace ForgeTekApplicationReleaseManager.Models;

/// <summary>Per-file verdict across the compared sources.</summary>
public enum CompareStatus
{
    /// <summary>Present in every compared source with the same content hash.</summary>
    Identical,
    /// <summary>Present in every compared source but the hashes differ.</summary>
    Differs,
    /// <summary>Missing from at least one compared source.</summary>
    Partial,
}

/// <summary>One file's hashes across the three possible sources (null = absent from that source).</summary>
public sealed record SourceFileRow(
    string Path,
    string? FilesHash,
    string? SlnHash,
    string? GitHubHash,
    CompareStatus Status);

/// <summary>Result of comparing an app's tracked files against its solution build and/or GitHub build.</summary>
public sealed class ComparisonReport
{
    public List<SourceFileRow> Rows { get; init; } = [];
    public bool HasFiles { get; init; }
    public bool HasSln { get; init; }
    public bool HasGitHub { get; init; }
    public string? Error { get; init; }

    public bool Ran => Error is null;
    public int Identical => Rows.Count(r => r.Status == CompareStatus.Identical);
    public int Differs   => Rows.Count(r => r.Status == CompareStatus.Differs);
    public int Partial   => Rows.Count(r => r.Status == CompareStatus.Partial);
    public int Total     => Rows.Count;
    public bool AllIdentical => Total > 0 && Differs == 0 && Partial == 0;
}
