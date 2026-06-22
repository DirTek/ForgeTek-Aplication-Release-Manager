namespace ForgeTekUpdatePackager.Services;

public interface ILogService
{
    void Write(string category, string message);

    /// <summary>Folder where daily log files are written.</summary>
    string LogFolder { get; }

    /// <summary>Returns the most recent log lines (newest last), across the latest day files, up to maxLines.</summary>
    IReadOnlyList<string> ReadRecent(int maxLines = 500);

    /// <summary>Returns log lines (chronological, newest last) for the day files in the inclusive date range,
    /// each prefixed with its date. Capped to <paramref name="maxLines"/> (keeping the newest).</summary>
    IReadOnlyList<string> ReadRange(DateOnly from, DateOnly to, int maxLines = 5000);
}
