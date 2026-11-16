using System.IO;
using System.Text.RegularExpressions;

namespace ForgeTekApplicationReleaseManager.Services;

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

    public string UpsertChangelog(string? existing, string versionSection, string versionNumber, string appName)
    {
        if (string.IsNullOrWhiteSpace(existing) || string.IsNullOrWhiteSpace(versionNumber))
            return BuildChangelog(existing, versionSection, appName);

        var lines = existing.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var startPattern = HeadingPattern(versionNumber.Trim());
        var start = Array.FindIndex(lines, l => Regex.IsMatch(l, startPattern, RegexOptions.IgnoreCase));
        if (start < 0)
            return BuildChangelog(existing, versionSection, appName);

        // The section runs until the next heading of the same or higher level (sub-headings are part of it).
        const string anyHeading = @"^\s{0,3}(#{1,6})\s+";
        var level = Regex.Match(lines[start], anyHeading).Groups[1].Value.Length;
        var end = start + 1;
        while (end < lines.Length)
        {
            var h = Regex.Match(lines[end], anyHeading);
            if (h.Success && h.Groups[1].Value.Length <= level) break;
            end++;
        }

        var before = string.Join("\n", lines.Take(start)).TrimEnd();
        var after  = string.Join("\n", lines.Skip(end)).TrimEnd();
        var result = (before.Length > 0 ? before + "\n\n" : "") + versionSection.Trim();
        if (after.Length > 0) result += "\n\n" + after;
        return result + "\n";
    }

    public IReadOnlyList<string> WriteSection(string? appFolder, string? sourceFolder,
        string versionSection, string versionNumber, string appName)
    {
        var written = new List<string>();
        var sourcePath = string.IsNullOrWhiteSpace(sourceFolder) ? null : FindChangelogFile(sourceFolder);
        var outputPath = string.IsNullOrWhiteSpace(appFolder) ? null : FindChangelogFile(appFolder);

        if (sourcePath is not null)
        {
            // The source changelog is the one the build bundles — update it, then mirror it into the build
            // output (when the app ships one there) so the version being scanned carries the same file a
            // rebuild would produce.
            Upsert(sourcePath);
            written.Add(sourcePath);
            if (outputPath is not null && !SamePath(outputPath, sourcePath))
            {
                File.Copy(sourcePath, outputPath, overwrite: true);
                written.Add(outputPath);
            }
            return written;
        }

        if (string.IsNullOrWhiteSpace(appFolder))
            throw new InvalidOperationException("No app folder or source folder to write the changelog to.");

        var path = outputPath ?? Path.Combine(appFolder, "CHANGELOG.md");
        Upsert(path);
        written.Add(path);
        return written;

        void Upsert(string p)
        {
            var existing = File.Exists(p) ? File.ReadAllText(p) : null;
            File.WriteAllText(p, UpsertChangelog(existing, versionSection, versionNumber, appName));
        }
    }

    public IReadOnlySet<string> GetDocumentedEntries(params string?[] folders)
    {
        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            var path = FindChangelogFile(folder);
            if (path is null) continue;
            foreach (var line in File.ReadAllLines(path))
            {
                var m = Regex.Match(line, @"^\s*[-*+]\s+(?<text>.+)$");
                if (!m.Success) continue;
                var key = NormalizeEntry(m.Groups["text"].Value);
                if (key.Length > 0) entries.Add(key);
            }
        }
        return entries;
    }

    /// <summary>Comparison key for a changelog line: lower-case letters and digits only, so wording that
    /// differs just by punctuation, casing or spacing is treated as the same entry.</summary>
    public static string NormalizeEntry(string text)
    {
        var chars = text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
