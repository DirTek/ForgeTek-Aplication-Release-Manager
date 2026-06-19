using System.IO;
using System.Text.RegularExpressions;

namespace ForgeTekUpdatePackager.Services;

public class ChangelogService : IChangelogService
{
    // Accept common changelog filenames (changelog.md / .txt / no extension), case-insensitive.
    private static readonly string[] ChangelogNames =
        { "changelog.md", "changelog.txt", "changelog", "changes.md", "history.md" };

    public string? FindChangelogFile(string appFolder)
    {
        if (!Directory.Exists(appFolder)) return null;
        return Directory.GetFiles(appFolder, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => ChangelogNames.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));
    }

    // Matches any markdown heading (#..######) whose text contains the version as a standalone token,
    // tolerating the common variants: "## Version 1.0.1 - x", "## [1.0.1] - x", "## v1.0.1", "## 1.0.1".
    // The look-around stops "1.0.1" from matching inside "1.0.10" or "11.0.1".
    private static string HeadingPattern(string versionNumber) =>
        $@"^\s{{0,3}}#{{1,6}}\s+.*(?<![\d.]){Regex.Escape(versionNumber)}(?![\d.])";

    public bool HasChangelogEntry(string changelogPath, string versionNumber)
    {
        if (!File.Exists(changelogPath) || string.IsNullOrWhiteSpace(versionNumber))
            return false;
        var pattern = HeadingPattern(versionNumber);
        return File.ReadAllLines(changelogPath).Any(line => Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase));
    }

    public string? ExtractVersionContent(string changelogPath, string versionNumber)
    {
        if (!File.Exists(changelogPath) || string.IsNullOrWhiteSpace(versionNumber))
            return null;

        var lines = File.ReadAllLines(changelogPath);
        var startPattern = HeadingPattern(versionNumber);
        const string anyHeading = @"^\s{0,3}(#{1,6})\s+";

        var result = new List<string>();
        var startLevel = -1;
        foreach (var line in lines)
        {
            if (startLevel < 0)
            {
                var start = Regex.Match(line, startPattern, RegexOptions.IgnoreCase);
                if (start.Success)
                {
                    startLevel = Regex.Match(line, anyHeading).Groups[1].Value.Length;
                    result.Add(line);
                }
                continue;
            }

            // End the section at the next heading of the same or higher level (sub-headings are kept).
            var heading = Regex.Match(line, anyHeading);
            if (heading.Success && heading.Groups[1].Value.Length <= startLevel)
                break;

            result.Add(line);
        }
        return result.Count > 0 ? string.Join(Environment.NewLine, result) : null;
    }

    public string BuildChangelog(string? existing, string versionSection, string appName)
    {
        versionSection = versionSection.Trim();

        if (string.IsNullOrWhiteSpace(existing))
        {
            var subject = string.IsNullOrWhiteSpace(appName) ? "this project" : appName.Trim();
            return $"# Changelog\n\nAll notable changes to {subject} are documented in this file.\n\n{versionSection}\n";
        }

        var text = existing.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n');

        // Insert above the first existing version heading ("## …"), keeping any "# Changelog" preamble.
        var idx = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^\s{0,3}##\s"));
        if (idx < 0)
            return text.TrimEnd() + "\n\n" + versionSection + "\n";

        var before = string.Join("\n", lines.Take(idx)).TrimEnd();
        var after  = string.Join("\n", lines.Skip(idx)).TrimEnd();
        return before + "\n\n" + versionSection + "\n\n" + after + "\n";
    }
}
