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

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}
