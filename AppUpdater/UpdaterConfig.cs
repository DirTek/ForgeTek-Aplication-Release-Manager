using System.IO;
using System.Text.Json;

namespace AppUpdater;

/// <summary>
/// Per-app updater configuration. Read from an <c>updater.json</c> sidecar placed next to the
/// updater EXE by FARM's generator, falling back to baked defaults when a field is absent. This
/// single mechanism lets the SAME binary serve as both the per-app branded build and the generic
/// prebuilt fallback — FARM only has to write the sidecar.
/// </summary>
public sealed class UpdaterConfig
{
    /// <summary>App identifier — must match the package header's <c>appKey</c>.</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>The main application executable name (e.g. "STLVerse.exe"), relative to the install dir.</summary>
    public string AppExe { get; set; } = string.Empty;

    /// <summary>This updater's own executable name. Defaults to the running process name at load time.</summary>
    public string UpdaterExe { get; set; } = string.Empty;

    /// <summary>Package file extension WITHOUT a leading dot (e.g. "ftu", "stlv").</summary>
    public string PackageExtension { get; set; } = "ftu";

    /// <summary>Folder (relative to the install dir) where packages + the plan are staged.</summary>
    public string UpdatesFolder { get; set; } = "Updates";

    /// <summary>Handoff plan file name written by the app's update service.</summary>
    public string PlanFile { get; set; } = "update-plan.json";

    /// <summary>Accent color (hex) for the window's progress + buttons.</summary>
    public string AccentColor { get; set; } = "#0A84FF";

    /// <summary>Window title / heading. Defaults to "{AppKey} Updater" when blank.</summary>
    public string WindowTitle { get; set; } = string.Empty;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Loads <c>updater.json</c> from <paramref name="baseDir"/> if present; otherwise returns the
    /// baked defaults. Never throws — a malformed sidecar falls back to defaults.
    /// </summary>
    public static UpdaterConfig Load(string baseDir)
    {
        var cfg = new UpdaterConfig();
        try
        {
            var path = Path.Combine(baseDir, "updater.json");
            if (File.Exists(path))
                cfg = JsonSerializer.Deserialize<UpdaterConfig>(File.ReadAllText(path), Options) ?? cfg;
        }
        catch { /* fall back to defaults */ }

        if (string.IsNullOrWhiteSpace(cfg.PackageExtension)) cfg.PackageExtension = "ftu";
        cfg.PackageExtension = cfg.PackageExtension.TrimStart('.');
        if (string.IsNullOrWhiteSpace(cfg.UpdatesFolder)) cfg.UpdatesFolder = "Updates";
        if (string.IsNullOrWhiteSpace(cfg.PlanFile)) cfg.PlanFile = "update-plan.json";
        if (string.IsNullOrWhiteSpace(cfg.AccentColor)) cfg.AccentColor = "#0A84FF";
        if (string.IsNullOrWhiteSpace(cfg.WindowTitle))
            cfg.WindowTitle = string.IsNullOrWhiteSpace(cfg.AppKey) ? "Updater" : $"{cfg.AppKey} Updater";

        return cfg;
    }
}
