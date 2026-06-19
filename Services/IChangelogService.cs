namespace ForgeTekUpdatePackager.Services;

public interface IChangelogService
{
    string? FindChangelogFile(string appFolder);
    bool HasChangelogEntry(string changelogPath, string versionNumber);
    string? ExtractVersionContent(string changelogPath, string versionNumber);

    /// <summary>Returns the full changelog text with <paramref name="versionSection"/> inserted as the
    /// newest entry (above existing "## " version sections, below the "# Changelog" header). Creates a
    /// fresh changelog with a header when <paramref name="existing"/> is null/empty.</summary>
    string BuildChangelog(string? existing, string versionSection, string appName);
}
