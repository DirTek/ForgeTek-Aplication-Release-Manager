using System.IO;
using System.Linq;

namespace ForgeTekUpdatePackager.Services;

public class LogService : ILogService
{
    private readonly string _logFolder;

    public LogService(ISettingsService settings)
    {
        _logFolder = Path.Combine(settings.RootFolder, "logs");
    }

    public string LogFolder => _logFolder;

    public IReadOnlyList<string> ReadRecent(int maxLines = 500)
    {
        try
        {
            if (!Directory.Exists(_logFolder)) return [];

            // Walk day files newest-first, accumulating lines until we hit the cap, then
            // return them in chronological order (newest last).
            var files = new DirectoryInfo(_logFolder)
                .GetFiles("*.log")
                .OrderByDescending(f => f.Name);

            var collected = new List<string>();
            foreach (var f in files)
            {
                // Each daily file is named yyyy-MM-dd.log; the lines themselves only carry a time,
                // so prefix the file's date to make the viewer show a full date + time stamp.
                var date = Path.GetFileNameWithoutExtension(f.Name);
                var lines = File.ReadAllLines(f.FullName).Select(l => $"{date} {l}").ToArray();
                // Prepend older-file blocks ahead of what we've already gathered.
                collected.InsertRange(0, lines);
                if (collected.Count >= maxLines) break;
            }

            return collected.Count > maxLines
                ? collected.Skip(collected.Count - maxLines).ToList()
                : collected;
        }
        catch { return []; }
    }

    public IReadOnlyList<string> ReadRange(DateOnly from, DateOnly to, int maxLines = 5000)
    {
        try
        {
            if (!Directory.Exists(_logFolder)) return [];
            if (from > to) (from, to) = (to, from);

            var collected = new List<string>();
            var files = new DirectoryInfo(_logFolder).GetFiles("*.log")
                .Select(f => (file: f, date: ParseDate(f.Name)))
                .Where(x => x.date is { } d && d >= from && d <= to)
                .OrderBy(x => x.date);   // chronological (oldest day first)

            foreach (var (file, date) in files)
            {
                var prefix = date!.Value.ToString("yyyy-MM-dd");
                foreach (var line in File.ReadAllLines(file.FullName))
                    collected.Add($"{prefix} {line}");
            }

            return collected.Count > maxLines
                ? collected.Skip(collected.Count - maxLines).ToList()
                : collected;
        }
        catch { return []; }
    }

    // Daily files are named yyyy-MM-dd.log; returns null for anything that doesn't match.
    private static DateOnly? ParseDate(string fileName)
        => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(fileName), "yyyy-MM-dd", out var d) ? d : null;

    public void Write(string category, string message)
    {
        var ts    = DateTime.Now.ToString("HH:mm:ss.fff");
        var line  = $"[{ts}] [{category}] {message}";
        var file  = Path.Combine(_logFolder, $"{DateTime.Today:yyyy-MM-dd}.log");
        try
        {
            Directory.CreateDirectory(_logFolder);
            File.AppendAllText(file, line + Environment.NewLine);
        }
        catch { /* never crash the app due to logging */ }
    }
}
