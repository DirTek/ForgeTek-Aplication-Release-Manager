using System.IO;
using System.Text.Json;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public class ManifestService : IManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<string> GenerateAsync(
        AppEntry entry,
        AppVersion version,
        IReadOnlyList<FileRecord> records,
        IReadOnlyList<string>? removedFiles,
        IProgress<string> progress,
        CancellationToken ct = default)
    {
        var files = new List<ManifestFileEntry>(records.Count);

        for (int i = 0; i < records.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var record = records[i];
            var fullPath = Path.Combine(entry.FolderPath, record.Path);

            progress.Report($"[{i + 1}/{records.Count}] {Path.GetFileName(record.Path)}");

            if (!File.Exists(fullPath))
            {
                progress.Report("  ⚠ File not found on disk, skipping");
                continue;
            }

            files.Add(new ManifestFileEntry
            {
                Path = record.Path.Replace('\\', '/'),
                Hash = $"sha256-{record.Checksum}",
                Size = new FileInfo(fullPath).Length,
            });
        }

        var manifest = new AppManifest
        {
            Version = version.VersionNumber,
            App = entry.Name,
            CreatedAt = DateTimeOffset.UtcNow,
            Files = files,
            RemovedFiles = removedFiles?.Select(p => p.Replace('\\', '/')).ToList() ?? [],
            Totals = new ManifestTotals
            {
                FileCount = files.Count,
                TotalSize = files.Sum(f => f.Size),
            },
        };

        return await Task.Run(() => JsonSerializer.Serialize(manifest, JsonOptions), ct);
    }
}
