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
