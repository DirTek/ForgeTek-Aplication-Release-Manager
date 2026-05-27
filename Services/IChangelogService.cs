namespace ForgeTekUpdatePackager.Services;

public interface IChangelogService
{
    string? FindChangelogFile(string appFolder);
    bool HasChangelogEntry(string changelogPath, string versionNumber);
    string? ExtractVersionContent(string changelogPath, string versionNumber);
}
