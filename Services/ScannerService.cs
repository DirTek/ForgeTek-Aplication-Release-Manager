using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services;

public class ScannerService : IScannerService
{
    // Build artifacts auto-flagged as "exclude from package" on scan (the user can still toggle).
    private static readonly HashSet<string> DebugExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".pdb", ".ilk", ".exp", ".map" };

    public IReadOnlyList<FileRecord> ScanDirectory(
        string folderPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var folder = new DirectoryInfo(folderPath);
        if (!folder.Exists)
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

        // Materialise up-front so we have a total count for the progress bar.
        var allFiles = folder.EnumerateFiles("*", SearchOption.AllDirectories)
                             .OrderBy(f => f.FullName)
                             .ToList();

        int total     = allFiles.Count;
        int processed = 0;
        var records   = new List<FileRecord>(total);

        foreach (var file in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(folderPath, file.FullName);
            try
            {
                records.Add(new FileRecord
                {
                    Path         = relativePath,
                    Checksum     = ComputeChecksum(file.FullName),
                    DateModified = file.LastWriteTime,
                    IsDebug      = DebugExtensions.Contains(file.Extension),
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            progress?.Report(new ScanProgress(total, ++processed, relativePath));
        }

        return records;
    }

    public static string ComputeChecksum(string path)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Tries to read a 3-component file version (major.minor.build) from an .exe in
    /// <paramref name="folderPath"/>. Prefers an exe whose name matches <paramref name="appName"/>;
    /// falls back to the only exe if exactly one exists.
    /// Returns null when no unambiguous exe is found or the version cannot be read.
    /// Call <see cref="FindRootExeFiles"/> first when you need to handle the multi-exe ambiguous case.
    /// </summary>
    public static string? DetectExeVersion(string folderPath, string appName)
    {
        string[] exes;
        try { exes = Directory.GetFiles(folderPath, "*.exe", SearchOption.TopDirectoryOnly); }
        catch { return null; }

        if (exes.Length == 0) return null;

        var appKey = appName.Replace(" ", "").ToLowerInvariant();
        var match  = exes.FirstOrDefault(e =>
            Path.GetFileNameWithoutExtension(e).Replace(" ", "").ToLowerInvariant() == appKey);

        var target = match ?? (exes.Length == 1 ? exes[0] : null);
        return target is null ? null : ReadExeVersion(target);
    }

    public static string? ReadExeVersion(string fullPath)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(fullPath);
            if (string.IsNullOrWhiteSpace(fvi.FileVersion)) return null;
            var parts = fvi.FileVersion.Split('.');
            return string.Join('.', parts.Take(3).Select(p => p.Trim()));
        }
        catch { return null; }
    }

    // Explicit interface implementations delegate to existing static methods.
    // Callers using IScannerService go through these; existing static callers are unaffected.
    string IScannerService.ComputeChecksum(string path) => ComputeChecksum(path);
    string? IScannerService.DetectExeVersion(string folderPath, string appName) => DetectExeVersion(folderPath, appName);
    string? IScannerService.ReadExeVersion(string fullPath) => ReadExeVersion(fullPath);
    IReadOnlyList<string> IScannerService.FindRootExeFiles(string folderPath) => FindRootExeFiles(folderPath);

    /// <summary>Returns filenames (not full paths) of all .exe files in the root of the folder.</summary>
    public static IReadOnlyList<string> FindRootExeFiles(string folderPath)
    {
        try
        {
            return Directory.GetFiles(folderPath, "*.exe", SearchOption.TopDirectoryOnly)
                            .Select(Path.GetFileName).OfType<string>().ToList();
        }
        catch { return []; }
    }

    public DiffResult DiffVersions(
        AppVersion baseVersion,
        IReadOnlyList<FileRecord> newFiles,
        IProgress<DiffProgress>? progress = null)
    {
        progress?.Report(new DiffProgress("Building debug path set…", 0));
        var baseDebugPaths = baseVersion.Files
            .Where(f => f.IsDebug)
            .Select(f => f.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        progress?.Report(new DiffProgress("Building base file map…", 25));
        var baseMap = baseVersion.NonDebugFiles.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

        progress?.Report(new DiffProgress("Building new file map…", 50));
        // Shipped files: not excluded (debug), not marked-for-removal, not a baseline debug path.
        var newMap = newFiles
            .Where(f => !f.IsDebug && !f.IsRemoved && !baseDebugPaths.Contains(f.Path))
            .ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

        // Every path in the new scan. A baseline file still present (and not marked-removed) was NOT
        // deleted — it's either shipped or excluded (kept on disk). Removed = gone OR user-marked.
        var newAllPaths      = newFiles.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newRemovedPaths  = newFiles.Where(f => f.IsRemoved).Select(f => f.Path)
                                       .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newExcludedPaths = newFiles.Where(f => f.IsDebug && !f.IsRemoved).Select(f => f.Path)
                                       .ToHashSet(StringComparer.OrdinalIgnoreCase);

        progress?.Report(new DiffProgress("Comparing files…", 75));
        var result = new DiffResult
        {
            Added    = [.. newMap.Where(kv => !baseMap.ContainsKey(kv.Key)).Select(kv => kv.Value)],
            // Deleted on clients: gone from the scan entirely, or explicitly marked for removal.
            Removed  = [.. baseMap.Where(kv => !newAllPaths.Contains(kv.Key) || newRemovedPaths.Contains(kv.Key)).Select(kv => kv.Value)],
            // Was shipped before, now excluded — kept on disk, neither shipped nor deleted.
            Excluded = [.. baseMap.Where(kv => newExcludedPaths.Contains(kv.Key)).Select(kv => kv.Value)],
            Modified = [.. newMap.Where(kv => baseMap.TryGetValue(kv.Key, out var b)
                            && b.Checksum != kv.Value.Checksum).Select(kv => kv.Value)],
            Unchanged= [.. newMap.Where(kv => baseMap.TryGetValue(kv.Key, out var b)
                            && b.Checksum == kv.Value.Checksum).Select(kv => kv.Value)],
        };

        progress?.Report(new DiffProgress("Done.", 100));
        return result;
    }
}

public record DiffResult
{
    public List<FileRecord> Added { get; init; } = [];
    public List<FileRecord> Removed { get; init; } = [];
    /// <summary>Files that were shipped before but are now excluded — kept on disk, not shipped, not deleted.</summary>
    public List<FileRecord> Excluded { get; init; } = [];
    public List<FileRecord> Modified { get; init; } = [];
    public List<FileRecord> Unchanged { get; init; } = [];
}

public record ScanProgress(int TotalFiles, int ProcessedFiles, string CurrentFile)
{
    public double Percentage => TotalFiles == 0 ? 0d : (double)ProcessedFiles / TotalFiles * 100d;
}

public record DiffProgress(string CurrentPhase, double Percentage);
