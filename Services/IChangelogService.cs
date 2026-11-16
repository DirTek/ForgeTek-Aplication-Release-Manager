namespace ForgeTekApplicationReleaseManager.Services;

public interface IChangelogService
{
    string? FindChangelogFile(string appFolder);
    bool HasChangelogEntry(string changelogPath, string versionNumber);
    string? ExtractVersionContent(string changelogPath, string versionNumber);

    /// <summary>Returns the full changelog text with <paramref name="versionSection"/> inserted as the
    /// newest entry (above existing "## " version sections, below the "# Changelog" header). Creates a
    /// fresh changelog with a header when <paramref name="existing"/> is null/empty.</summary>
    string BuildChangelog(string? existing, string versionSection, string appName);

    /// <summary>Like <see cref="BuildChangelog"/>, but when <paramref name="existing"/> already has a section
    /// for <paramref name="versionNumber"/> that section is replaced in place instead of adding a duplicate.</summary>
    string UpsertChangelog(string? existing, string versionSection, string versionNumber, string appName);

    /// <summary>Writes <paramref name="versionSection"/> into the app's changelog. When the source folder
    /// has a changelog it is updated (that is the file the build bundles) and copied over the build-output
    /// changelog if there is one; otherwise the build-output changelog is updated (created if missing).
    /// Returns the paths written.</summary>
    IReadOnlyList<string> WriteSection(string? appFolder, string? sourceFolder,
        string versionSection, string versionNumber, string appName);

    /// <summary>Normalized (<see cref="ChangelogService.NormalizeEntry"/>) bullet entries already present in
    /// the changelogs of <paramref name="folders"/> — used to hide suggestions documented in earlier versions.</summary>
    IReadOnlySet<string> GetDocumentedEntries(params string?[] folders);
}
