using System.IO;

namespace ForgeTekUpdatePackager.Services;

public class LogService : ILogService
{
    private readonly string _logFolder;

    public LogService(ISettingsService settings)
    {
        _logFolder = Path.Combine(settings.RootFolder, "logs");
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
