namespace ForgeTekUpdatePackager.Models;

public class SetupBundle
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = string.Empty;
    public List<SetupAppRef> Apps { get; set; } = [];
    public List<RedistEntry> Redists { get; set; } = [];
    public bool SignOutput { get; set; }
    public string EulaText { get; set; } = string.Empty;
    public string? BannerImage { get; set; }
    /// <summary>Icon (.ico or .exe) baked into the generated setup EXE. Bundle-level, separate
    /// from each app's own icon. When null, falls back to the first app's icon.</summary>
    public string? SetupIconPath { get; set; }
    /// <summary>For multi-app bundles, which app the setup offers to launch on the final page
    /// (AppId). When null, no launch option is shown.</summary>
    public string? LaunchAppId { get; set; }
    /// <summary>The exe (within the launch app's files) the final page launches. When null, the
    /// first exe in that app's folder is used.</summary>
    public string? LaunchExeName { get; set; }

    // ── Setup window appearance ───────────────────────────────────────────────
    /// <summary>"Solid", "Gradient", "Image", or null/"Default" (the built-in dark theme).</summary>
    public string? BackgroundMode { get; set; }
    /// <summary>Solid color, or the gradient's start color (hex #RRGGBB).</summary>
    public string? BackgroundColor1 { get; set; }
    /// <summary>Gradient end color (hex #RRGGBB).</summary>
    public string? BackgroundColor2 { get; set; }
    /// <summary>"Vertical", "Horizontal", or "Diagonal".</summary>
    public string BackgroundGradientDirection { get; set; } = "Vertical";
    /// <summary>Path to a full-window background image (used when BackgroundMode == "Image").</summary>
    public string? BackgroundImage { get; set; }
    /// <summary>Lock the setup window to a fixed size (no resizing).</summary>
    public bool FixedSize { get; set; }

    /// <summary>Show a small attribution watermark in the installer window footer.</summary>
    public bool   ShowFooterWatermark { get; set; } = true;
    /// <summary>Text of the footer watermark (editable).</summary>
    public string FooterWatermark     { get; set; } = "Installer by ForgeTek Release Manager";
    /// <summary>Before overwriting an existing Setup.exe at the output folder, rename the old one to
    /// "{name}Setup-{previous generation date}.exe" so prior builds are kept as backups.</summary>
    public bool PreserveOldSetups { get; set; }

    public string? LastGeneratedPath { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime? LastGeneratedDate { get; set; }

    /// <summary>App version numbers captured at the last successful generation (AppId → version),
    /// so the list can show what actually shipped rather than each app's current latest.</summary>
    public Dictionary<string, string> GeneratedAppVersions { get; set; } = [];
}
