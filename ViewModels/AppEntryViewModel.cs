using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.ViewModels;

public enum VersionDisplayMode { None, Published, InProgress, Retracted }

public class AppEntryViewModel(AppEntry entry) : ObservableObject
{
    public AppEntry Entry { get; private set; } = entry;
    public string Name => Entry.Name;
    public string AccentColor => Entry.AccentColor;
    public SolidColorBrush AccentBrush => ParseColor(AccentColor);

    internal void SetEntry(AppEntry newEntry)
    {
        Entry = newEntry;
        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(AccentColor));
        OnPropertyChanged(nameof(AccentBrush));
        OnPropertyChanged(nameof(DisplayMode));
        OnPropertyChanged(nameof(CurrentVersionText));
        OnPropertyChanged(nameof(HasCurrentVersion));
        OnPropertyChanged(nameof(NextVersionText));
        OnPropertyChanged(nameof(NextVersionStatus));
        OnPropertyChanged(nameof(RetractedVersionText));
        OnPropertyChanged(nameof(PreviousVersionText));
        OnPropertyChanged(nameof(HasPreviousVersion));
    }

    public VersionDisplayMode DisplayMode
    {
        get
        {
            if (Entry.Versions.Count == 0) return VersionDisplayMode.None;
            var lastAny = Entry.Versions[^1];
            if (lastAny.Status is VersionStatus.Retracted or VersionStatus.Scrapped)
                return VersionDisplayMode.Retracted;
            if (lastAny.Status != VersionStatus.Published)
                return VersionDisplayMode.InProgress;
            return VersionDisplayMode.Published;
        }
    }

    // Latest published version — used as the "current" base in Published and InProgress modes
    public string? CurrentVersionText
    {
        get
        {
            var v = Entry.Versions.LastOrDefault(v => v.Status == VersionStatus.Published);
            return v is not null ? $"v{v.VersionNumber}" : null;
        }
    }

    public bool HasCurrentVersion => CurrentVersionText is not null;

    // In-progress version (InProgress mode)
    public string? NextVersionText
    {
        get
        {
            var latest = Entry.LatestVersion;
            if (latest is null) return null;
            if (latest.Status is VersionStatus.Published or VersionStatus.Retracted or VersionStatus.Scrapped) return null;
            return $"v{latest.VersionNumber}";
        }
    }

    public VersionStatus? NextVersionStatus
    {
        get
        {
            var latest = Entry.LatestVersion;
            if (latest is null) return null;
            if (latest.Status is VersionStatus.Published or VersionStatus.Retracted or VersionStatus.Scrapped) return null;
            return latest.Status;
        }
    }

    // Retracted / scrapped version (Retracted mode)
    public string? RetractedVersionText
    {
        get
        {
            if (Entry.Versions.Count == 0) return null;
            var last = Entry.Versions[^1];
            return last.Status is VersionStatus.Retracted or VersionStatus.Scrapped
                ? $"v{last.VersionNumber}" : null;
        }
    }

    // Last published version before the retracted one (Retracted mode, shown after →)
    public string? PreviousVersionText
    {
        get
        {
            if (Entry.Versions.Count < 2) return null;
            var last = Entry.Versions[^1];
            if (last.Status is not (VersionStatus.Retracted or VersionStatus.Scrapped)) return null;
            var prev = Entry.Versions
                .Take(Entry.Versions.Count - 1)
                .LastOrDefault(v => v.Status == VersionStatus.Published);
            return prev is not null ? $"v{prev.VersionNumber}" : null;
        }
    }

    public bool HasPreviousVersion => PreviousVersionText is not null;

    private static SolidColorBrush ParseColor(string hex)
    {
        if (string.IsNullOrEmpty(hex)) hex = "#0A84FF";
        hex = hex.TrimStart('#');
        if (hex.Length >= 6 && byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
                           && byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
                           && byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
            return new SolidColorBrush(Color.FromRgb(r, g, b));
        return new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
    }
}
