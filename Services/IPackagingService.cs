using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public interface IPackagingService
{
    Task<string> BuildAsync(AppEntry entry, AppVersion version, IReadOnlyList<FileRecord> files, PackageType packageType, string outputPath, string? manifestPath, IReadOnlyList<string>? removedFiles, IProgress<string> progress, CancellationToken ct = default);
    Task VerifyAsync(string filePath, IProgress<string> progress, CancellationToken ct = default);
}
