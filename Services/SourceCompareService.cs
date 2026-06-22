using System.IO;
using System.Security.Cryptography;
using System.Text;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

/// <summary>Compares the file sets and SHA256 content hashes of the app's tracked files, its solution
/// build output, and its GitHub build output — a "SLN = files = GitHub" integrity check.</summary>
public sealed class SourceCompareService : ISourceCompareService
{
    // Debug symbols aren't part of a release and add noise to the comparison.
    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".pdb" };

    public Task<ComparisonReport> CompareAsync(string? filesDir, string? slnDir, string? githubDir,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var files = HashDir(filesDir, progress, ct);
            var sln = HashDir(slnDir, progress, ct);
            var github = HashDir(githubDir, progress, ct);

            var provided = new[] { filesDir, slnDir, githubDir }.Count(d => !string.IsNullOrWhiteSpace(d));
            if (provided < 2)
                return new ComparisonReport { Error = "Pick at least two sources to compare." };

            return Compare(files, sln, github);
        }, ct);

    private static IReadOnlyDictionary<string, string>? HashDir(string? dir,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        if (!Directory.Exists(dir)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (IgnoredExtensions.Contains(Path.GetExtension(file))) continue;
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            try { map[rel] = HashUtil.Sha256File(file); }
            catch { /* unreadable/locked file — skip */ }
        }
        progress?.Report($"Hashed {map.Count} file(s) in {dir}");
        return map;
    }

    /// <summary>Compares a local working copy against a GitHub repo tree by git blob SHA-1, so you can
    /// see whether your local project matches what's on GitHub. Only files tracked in the repo are
    /// considered (build artifacts / untracked files are ignored). Files=local, GitHub=remote.</summary>
    public Task<ComparisonReport> CompareWithTreeAsync(string localDir,
        IReadOnlyList<RepoTreeEntry> tree, IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(localDir) || !Directory.Exists(localDir))
                return new ComparisonReport { Error = "The local project folder was not found." };

            var remote = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in tree)
            {
                ct.ThrowIfCancellationRequested();
                remote[entry.Path] = entry.Sha;
                var full = Path.Combine(localDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                {
                    try { local[entry.Path] = GitBlobSha1(full); } catch { /* unreadable — treat as missing */ }
                }
            }
            progress?.Report($"Compared {remote.Count} tracked file(s) against {localDir}");
            return Compare(local, sln: null, github: remote);
        }, ct);

    /// <summary>The git blob SHA-1 of a file: SHA1("blob {len}\0" + content), lowercase hex.</summary>
    public static string GitBlobSha1(string path)
    {
        var content = File.ReadAllBytes(path);
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        using var sha1 = SHA1.Create();
        sha1.TransformBlock(header, 0, header.Length, null, 0);
        sha1.TransformFinalBlock(content, 0, content.Length);
        return Convert.ToHexString(sha1.Hash!).ToLowerInvariant();
    }

    /// <summary>Pure comparison over already-hashed sources (null = source not included). Testable.</summary>
    public static ComparisonReport Compare(
        IReadOnlyDictionary<string, string>? files,
        IReadOnlyDictionary<string, string>? sln,
        IReadOnlyDictionary<string, string>? github)
    {
        var active = new List<IReadOnlyDictionary<string, string>>();
        if (files is not null) active.Add(files);
        if (sln is not null) active.Add(sln);
        if (github is not null) active.Add(github);

        var allPaths = active
            .SelectMany(m => m.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

        var rows = new List<SourceFileRow>();
        foreach (var path in allPaths)
        {
            var fh = Get(files, path);
            var sh = Get(sln, path);
            var gh = Get(github, path);

            var presentInAll = active.All(m => m.ContainsKey(path));
            var distinctHashes = active
                .Where(m => m.ContainsKey(path))
                .Select(m => m[path])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            var status = !presentInAll ? CompareStatus.Partial
                : distinctHashes == 1 ? CompareStatus.Identical
                : CompareStatus.Differs;

            rows.Add(new SourceFileRow(path, fh, sh, gh, status));
        }

        return new ComparisonReport
        {
            Rows = rows,
            HasFiles = files is not null,
            HasSln = sln is not null,
            HasGitHub = github is not null,
        };

        static string? Get(IReadOnlyDictionary<string, string>? map, string path)
            => map is not null && map.TryGetValue(path, out var h) ? h : null;
    }
}
