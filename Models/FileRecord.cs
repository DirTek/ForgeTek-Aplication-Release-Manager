namespace ForgeTekUpdatePackager.Models;

public class FileRecord
{
    public string Path { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
    public DateTime DateModified { get; set; }
    public bool IsDebug { get; set; }
}
