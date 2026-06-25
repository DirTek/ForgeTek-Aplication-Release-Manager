using System.IO;
using System.Text.Json;

namespace AppUpdater;

/// <summary>
/// Per-app updater configuration. FARM bakes this JSON into the updater EXE itself (appended after
/// the file with a magic footer — see <see cref="EmbedMagic"/>), so the updater ships as a single
/// file. At runtime it is read from the EXE first; a sibling <c>updater.json</c> sidecar is still
/// honoured as an optional override (and for older two-file builds), then baked defaults.
/// </summary>
public sealed class UpdaterConfig
{
    /// <summary>Footer magic marking config appended to the EXE: <c>[json][int32 LE length][magic]</c>.</summary>
    private static readonly byte[] EmbedMagic = "FTUCFG01"u8.ToArray();

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
    /// Resolves the config in priority order: (a) a sibling <c>updater.json</c> sidecar in
    /// <paramref name="baseDir"/> (optional manual override), (b) config embedded in this EXE (FARM's
    /// default — single file), (c) baked defaults. Never throws — anything unreadable falls through.
    /// </summary>
    public static UpdaterConfig Load(string baseDir)
    {
        var cfg = new UpdaterConfig();
        try
        {
            var json = TryReadSidecar(baseDir) ?? TryReadEmbeddedConfig(Environment.ProcessPath);
            if (json is not null)
                cfg = JsonSerializer.Deserialize<UpdaterConfig>(json, Options) ?? cfg;
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

    private static string? TryReadSidecar(string baseDir)
    {
        var path = Path.Combine(baseDir, "updater.json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Reads config appended to the updater EXE: <c>[json][int32 LE length][8-byte magic]</c>.
    /// Returns null when the file has no such footer (a plain/dev build). Opens with shared
    /// read/write so reading our own running EXE never fails.</summary>
    private static string? TryReadEmbeddedConfig(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            return null;

        const int trailer = 4 + 8; // length + magic
        using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < trailer)
            return null;

        var tail = new byte[trailer];
        fs.Seek(-trailer, SeekOrigin.End);
        fs.ReadExactly(tail);

        for (var i = 0; i < EmbedMagic.Length; i++)
            if (tail[4 + i] != EmbedMagic[i])
                return null;

        var length = BitConverter.ToInt32(tail, 0);
        if (length <= 0 || length > fs.Length - trailer)
            return null;

        var json = new byte[length];
        fs.Seek(-(trailer + length), SeekOrigin.End);
        fs.ReadExactly(json);
        return System.Text.Encoding.UTF8.GetString(json);
    }
}
