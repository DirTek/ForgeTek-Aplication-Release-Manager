using System.IO;
using Xunit;
using ForgeTekApplicationReleaseManager.Services;

namespace ForgeTekApplicationReleaseManager.Tests;

public class ChangelogServiceTests
{
    private readonly ChangelogService _service = new();

    [Fact]
    public void BuildChangelog_CreatesHeaderWhenEmpty()
    {
        var result = _service.BuildChangelog(null, "## Version 1.0.0 - 2026-01-01\n\n### Added\n- First", "MyApp");
        Assert.StartsWith("# Changelog", result);
        Assert.Contains("MyApp", result);
        Assert.Contains("## Version 1.0.0 - 2026-01-01", result);
    }

    [Fact]
    public void BuildChangelog_PrependsNewestAboveExisting()
    {
        var existing =
            "# Changelog\n\nAll notable changes are documented here.\n\n## Version 1.0.0 - 2026-01-01\n\n### Added\n- First\n";
        var result = _service.BuildChangelog(existing, "## Version 1.1.0 - 2026-02-01\n\n### Fixed\n- A bug", "MyApp");

        // Header preserved once, and the new version appears before the old one.
        Assert.Equal(1, CountOccurrences(result, "# Changelog"));
        var idxNew = result.IndexOf("## Version 1.1.0", StringComparison.Ordinal);
        var idxOld = result.IndexOf("## Version 1.0.0", StringComparison.Ordinal);
        Assert.True(idxNew >= 0 && idxOld >= 0 && idxNew < idxOld);
    }

    [Fact]
    public void BuildChangelog_AppendsWhenNoExistingVersionSections()
    {
        var existing = "# Changelog\n\nPreamble only, no versions yet.\n";
        var result = _service.BuildChangelog(existing, "## Version 1.0.0 - 2026-01-01\n\n### Added\n- First", "MyApp");
        Assert.Contains("Preamble only", result);
        Assert.Contains("## Version 1.0.0", result);
    }

    [Fact]
    public void UpsertChangelog_ReplacesExistingSectionForSameVersion()
    {
        var existing =
            "# Changelog\n\n## Version 1.1.0 - 2026-02-01\n\n### Fixed\n- Old note\n\n## Version 1.0.0 - 2026-01-01\n\n### Added\n- First\n";
        var result = _service.UpsertChangelog(existing,
            "## Version 1.1.0 - 2026-02-02\n\n### Fixed\n- New note", "1.1.0", "MyApp");

        Assert.Equal(1, CountOccurrences(result, "## Version 1.1.0"));
        Assert.Contains("- New note", result);
        Assert.DoesNotContain("Old note", result);
        Assert.Contains("## Version 1.0.0 - 2026-01-01", result);   // older history untouched
        Assert.True(result.IndexOf("1.1.0", StringComparison.Ordinal) < result.IndexOf("1.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void UpsertChangelog_InsertsWhenVersionMissing()
    {
        var existing = "# Changelog\n\n## Version 1.0.0 - 2026-01-01\n\n- First\n";
        var result = _service.UpsertChangelog(existing, "## Version 1.0.1 - 2026-01-02\n\n- Second", "1.0.1", "MyApp");
        Assert.True(result.IndexOf("1.0.1", StringComparison.Ordinal) < result.IndexOf("1.0.0", StringComparison.Ordinal));
        Assert.Contains("- First", result);
    }

    [Fact]
    public void UpsertChangelog_DoesNotTreatLongerVersionAsSame()
    {
        var existing = "# Changelog\n\n## Version 1.0.10 - 2026-01-01\n\n- Ten\n";
        var result = _service.UpsertChangelog(existing, "## Version 1.0.1 - 2026-01-02\n\n- One", "1.0.1", "MyApp");
        Assert.Contains("- Ten", result);
        Assert.Contains("- One", result);
    }

    [Fact]
    public void WriteSection_UpdatesSourceAndMirrorsIntoOutput()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var src = Path.Combine(root, "src");
            var output = Path.Combine(src, "bin", "Release");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(src, "CHANGELOG.md"), "# Changelog\n\n## Version 1.0.0 - 2026-01-01\n\n- First\n");
            File.WriteAllText(Path.Combine(output, "CHANGELOG.md"), "stale copy from the last build");

            var written = _service.WriteSection(output, src, "## Version 1.0.1 - 2026-01-02\n\n- Second", "1.0.1", "MyApp");

            Assert.Equal(2, written.Count);
            var srcText = File.ReadAllText(Path.Combine(src, "CHANGELOG.md"));
            Assert.Contains("1.0.1", srcText);
            Assert.Contains("- First", srcText);
            Assert.Equal(srcText, File.ReadAllText(Path.Combine(output, "CHANGELOG.md")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WriteSection_FallsBackToOutputFolderWithoutSourceChangelog()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var written = _service.WriteSection(root, null, "## Version 1.0.0 - 2026-01-01\n\n- First", "1.0.0", "MyApp");
            Assert.Single(written);
            Assert.StartsWith("# Changelog", File.ReadAllText(Path.Combine(root, "CHANGELOG.md")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void GetDocumentedEntries_MatchesIgnoringCaseAndPunctuation()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "CHANGELOG.md"),
                "# Changelog\n\n## Version 1.0.0\n\n### Fixed\n- Fixed the FTP hang, when publishing.\n* Second item\n");
            var entries = _service.GetDocumentedEntries(root, null);
            Assert.Contains(ChangelogService.NormalizeEntry("fixed the ftp hang when publishing"), entries);
            Assert.Contains(ChangelogService.NormalizeEntry("Second item"), entries);
            Assert.DoesNotContain(ChangelogService.NormalizeEntry("Version 1.0.0"), entries);
        }
        finally { Directory.Delete(root, true); }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}
