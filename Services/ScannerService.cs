using System.IO;
using System.Security.Cryptography;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public class ScannerService
{
    public IReadOnlyList<FileRecord> ScanDirectory(string folderPath)
    {
        var folder = new DirectoryInfo(folderPath);
        if (!folder.Exists)
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

        var records = new List<FileRecord>();
        foreach (var file in folder.EnumerateFiles("*", SearchOption.AllDirectories)
                                   .OrderBy(f => f.FullName))
        {
            try
            {
                records.Add(new FileRecord
                {
                    Path = Path.GetRelativePath(folderPath, file.FullName),
                    Checksum = ComputeChecksum(file.FullName),
                    DateModified = file.LastWriteTime,
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return records;
    }

    private static string ComputeChecksum(string path)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    public DiffResult DiffVersions(AppVersion baseVersion, IReadOnlyList<FileRecord> newFiles)
    {
        var baseMap = baseVersion.NonDebugFiles.ToDictionary(f => f.Path);
        var newMap = newFiles.ToDictionary(f => f.Path);

        return new DiffResult
        {
            Added    = [.. newMap.Where(kv => !baseMap.ContainsKey(kv.Key)).Select(kv => kv.Value)],
            Removed  = [.. baseMap.Where(kv => !newMap.ContainsKey(kv.Key)).Select(kv => kv.Value)],
            Modified = [.. newMap.Where(kv => baseMap.TryGetValue(kv.Key, out var b)
                            && b.Checksum != kv.Value.Checksum).Select(kv => kv.Value)],
            Unchanged= [.. newMap.Where(kv => baseMap.TryGetValue(kv.Key, out var b)
                            && b.Checksum == kv.Value.Checksum).Select(kv => kv.Value)],
        };
    }
}

public record DiffResult
{
    public List<FileRecord> Added { get; init; } = [];
    public List<FileRecord> Removed { get; init; } = [];
    public List<FileRecord> Modified { get; init; } = [];
    public List<FileRecord> Unchanged { get; init; } = [];
}
