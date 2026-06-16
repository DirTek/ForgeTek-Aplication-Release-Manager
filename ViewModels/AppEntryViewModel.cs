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

    /// <summary>Subtle left-to-right wash of the app's accent color fading to transparent,
    /// used as the sidebar row background for a splash of per-app color.</summary>
    public LinearGradientBrush AccentGradientBrush
    {
        get
        {
            var c = ParseColor(AccentColor).Color;
            var brush = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint   = new System.Windows.Point(1, 0),
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x66, c.R, c.G, c.B), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 0.9));
            brush.Freeze();
            return brush;
        }
    }

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
        OnPropertyChanged(nameof(AccentGradientBrush));
        OnPropertyChanged(nameof(DisplayMode));
        OnPropertyChanged(nameof(CurrentVersionText));
        OnPropertyChanged(nameof(HasCurrentVersion));
        OnPropertyChanged(nameof(NextVersionText));
        OnPropertyChanged(nameof(NextVersionStatus));
        OnPropertyChanged(nameof(RetractedVersionText));
        OnPropertyChanged(nameof(PreviousVersionText));
        OnPropertyChanged(nameof(HasPreviousVersion));
        OnPropertyChanged(nameof(VersionLine));
        OnPropertyChanged(nameof(StatusBadge));
        OnPropertyChanged(nameof(NeedsAttention));
    }

    // ── Compact status, used by the Home dashboard cards ──────────────────

    /// <summary>One-line version summary for a card (e.g. "v0.1.37" or "v0.1.31 → v0.1.37").</summary>
    public string VersionLine => DisplayMode switch
    {
        VersionDisplayMode.None       => "No versions yet",
        VersionDisplayMode.InProgress => HasCurrentVersion ? $"{CurrentVersionText} → {NextVersionText}" : NextVersionText ?? string.Empty,
        VersionDisplayMode.Retracted  => HasPreviousVersion ? $"{RetractedVersionText} → {PreviousVersionText}" : RetractedVersionText ?? string.Empty,
        _                             => CurrentVersionText ?? string.Empty,
    };

    /// <summary>Short release-state label for the dashboard badge.</summary>
    public string StatusBadge => DisplayMode switch
    {
        VersionDisplayMode.None       => "No versions",
        VersionDisplayMode.Retracted  => "Retracted",
        VersionDisplayMode.InProgress => Entry.LatestVersion?.Status == VersionStatus.Packed ? "Ready to publish" : "In progress",
        _                             => "Published",
    };

    /// <summary>True when the app wants a nudge (no versions yet, or a build is ready to publish).</summary>
    public bool NeedsAttention =>
        DisplayMode == VersionDisplayMode.None
        || DisplayMode == VersionDisplayMode.Retracted
        || (DisplayMode == VersionDisplayMode.InProgress && Entry.LatestVersion?.Status == VersionStatus.Packed);

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
