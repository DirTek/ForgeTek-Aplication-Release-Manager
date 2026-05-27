namespace ForgeTekUpdatePackager.Models;

public class AppEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public string InitialVersion { get; set; } = string.Empty;
    public string AccentColor { get; set; } = "#0A84FF";
    public List<AppVersion> Versions { get; set; } = [];

    public AppVersion? LatestVersion => Versions
        .Where(v => v.Status != VersionStatus.Retracted && v.Status != VersionStatus.Scrapped)
        .LastOrDefault();
}
