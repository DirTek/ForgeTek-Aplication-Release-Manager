namespace ForgeTekUpdatePackager.Models;

public class SetupBundle
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = string.Empty;

    /// <summary>Optional output EXE name template with build variables (e.g. "{AppName}_{Version}_Setup").
    /// Blank uses the default "{AppName}Setup". The ".exe" extension is added automatically.</summary>
    public string? FileNameTemplate { get; set; }
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

    // ── Setup color theme + button style ──────────────────────────────────────
    // All nullable; when null the installer keeps its built-in dark palette.
    /// <summary>Primary button fill, progress bar, and highlight color (hex #RRGGBB).</summary>
    public string? AccentColor { get; set; }
    /// <summary>Button hover fill (hex #RRGGBB). Falls back to the accent color when null.</summary>
    public string? AccentHoverColor { get; set; }
    /// <summary>Primary button text/foreground color (hex #RRGGBB).</summary>
    public string? ButtonTextColor { get; set; }
    /// <summary>Main window text color (hex #RRGGBB).</summary>
    public string? TextColor { get; set; }
    /// <summary>Cards/panels/secondary-button surface color (hex #RRGGBB).</summary>
    public string? SurfaceColor { get; set; }
    /// <summary>Button corner style: "Rounded" (default), "Square", or "Pill".</summary>
    public string ButtonShape { get; set; } = "Rounded";

    // The installer footer attribution ("Installer by ForgeTek Release Manager") is fixed and applied
    // at generation time (SetupService.ForgeTekWatermark). It is intentionally not stored per-bundle
    // or operator-editable, so it can't be removed or rebranded.

    /// <summary>Before overwriting an existing Setup.exe at the output folder, rename the old one to
    /// "{name}Setup-{previous generation date}.exe" so prior builds are kept as backups.</summary>
    public bool PreserveOldSetups { get; set; }

    /// <summary>Pre/Post-install custom actions run by the installer (services, scripts, cleanup).</summary>
    public List<SetupCustomAction> CustomActions { get; set; } = [];

    /// <summary>Optional links shown as checkboxes on the installer's final page (open a website,
    /// open a readme). The user toggles which ones run when the setup finishes.</summary>
    public List<SetupCompletionAction> CompletionActions { get; set; } = [];

    /// <summary>Also emit a plain "{Name}_Portable.zip" of the app files (no installer, no registry).</summary>
    public bool GeneratePortableZip { get; set; }

    /// <summary>Where this bundle's generated setup is published — a target separate from the apps'
    /// update-publish settings (e.g. updates on FTP, the installer on GitHub Releases). Null = not set.
    /// Secret fields are DPAPI-protected at rest by SetupStorageService.</summary>
    public PublishProfile? PublishProfile { get; set; }

    public string? LastGeneratedPath { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime? LastGeneratedDate { get; set; }

    /// <summary>App version numbers captured at the last successful generation (AppId → version),
    /// so the list can show what actually shipped rather than each app's current latest.</summary>
    public Dictionary<string, string> GeneratedAppVersions { get; set; } = [];
}
