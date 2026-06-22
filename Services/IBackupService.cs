namespace ForgeTekUpdatePackager.Services;

public interface IBackupService
{
    Task CreateBackupAsync(string rootFolder, string globalSettingsFilePath, string outputPath,
        bool includeApps, bool includeSetups, IProgress<string> progress, CancellationToken ct);

    /// <summary>Re-imports a logical-export backup into the active store. Returns the number of restored users.</summary>
    Task<int> RestoreAsync(string zipPath, IProgress<string> progress, CancellationToken ct);
}
