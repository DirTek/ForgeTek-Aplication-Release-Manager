using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services;

public interface IPackagingService
{
    Task<string> BuildAsync(AppEntry entry, AppVersion version, IReadOnlyList<FileRecord> files, PackageType packageType, string outputPath, string? manifestPath, IReadOnlyList<string>? removedFiles, IProgress<string> progress, CancellationToken ct = default, IReadOnlyList<FileRecord>? expectedFiles = null);
    Task VerifyAsync(string filePath, IProgress<string> progress, CancellationToken ct = default);
}
