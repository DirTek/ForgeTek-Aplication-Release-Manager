using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.ViewModels;

public class AppEntryViewModel(AppEntry entry)
{
    public AppEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string VersionText => Entry.LatestVersion is { } v ? $"v{v.VersionNumber}" : "No versions";
}
