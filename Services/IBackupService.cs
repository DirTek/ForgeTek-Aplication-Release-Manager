namespace ForgeTekUpdatePackager.Services;

public interface IBackupService
{
    Task CreateBackupAsync(string rootFolder, string globalSettingsFilePath, string outputPath, IProgress<string> progress, CancellationToken ct);
}
