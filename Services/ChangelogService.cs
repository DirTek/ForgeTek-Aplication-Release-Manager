using System.IO;
using System.Text.RegularExpressions;

namespace ForgeTekUpdatePackager.Services;

public class ChangelogService
{
    public string? FindChangelogFile(string appFolder)
    {
        if (!Directory.Exists(appFolder)) return null;
        return Directory.GetFiles(appFolder, "*changelog*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => Path.GetFileName(f).Equals("changelog.md", StringComparison.OrdinalIgnoreCase));
    }

    public bool HasChangelogEntry(string changelogPath, string versionNumber)
    {
        if (!File.Exists(changelogPath) || string.IsNullOrWhiteSpace(versionNumber))
            return false;
        var lines = File.ReadAllLines(changelogPath);
        var pattern = $@"^\s*##\s+Version\s+{Regex.Escape(versionNumber)}\s+[-–—]";
        return lines.Any(line => Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase));
    }

    public string? ExtractVersionContent(string changelogPath, string versionNumber)
    {
        if (!File.Exists(changelogPath) || string.IsNullOrWhiteSpace(versionNumber))
            return null;
        var lines = File.ReadAllLines(changelogPath);
        var result = new List<string>();
        bool inSection = false;
        var startPattern    = $@"^\s*##\s+Version\s+{Regex.Escape(versionNumber)}\s+[-–—]";
        var nextVerPattern  = @"^\s*##\s+Version\s+";
        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, startPattern, RegexOptions.IgnoreCase))
            {
                inSection = true;
                result.Add(line);
                continue;
            }
            if (inSection)
            {
                if (Regex.IsMatch(line, nextVerPattern))
                    break;
                result.Add(line);
            }
        }
        return result.Count > 0 ? string.Join(Environment.NewLine, result) : null;
    }
}
