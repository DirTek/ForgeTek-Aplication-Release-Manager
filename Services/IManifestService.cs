using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services;

public interface IManifestService
{
    Task<string> GenerateAsync(AppEntry entry, AppVersion version, IReadOnlyList<FileRecord> records, IReadOnlyList<string>? removedFiles, IProgress<string> progress, CancellationToken ct = default);
}
