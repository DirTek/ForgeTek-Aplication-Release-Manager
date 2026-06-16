namespace ForgeTekUpdatePackager.Services;

public interface IBackupService
{
    Task CreateBackupAsync(string rootFolder, string globalSettingsFilePath, string outputPath,
        bool includeApps, bool includeSetups, IProgress<string> progress, CancellationToken ct);
}
