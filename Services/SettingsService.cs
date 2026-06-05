using System.IO;
using System.Text.Json;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public class SettingsService : ISettingsService
{
    private static readonly string GlobalSettingsPath = Path.Combine(
        AppContext.BaseDirectory, "settings", "global.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string GlobalSettingsFilePath => GlobalSettingsPath;

    public GlobalSettings Global { get; }
    public string RootFolder => Global.RootFolder;

    public SettingsService()
    {
        Global = LoadGlobal();
    }

    private static GlobalSettings LoadGlobal()
    {
        if (!File.Exists(GlobalSettingsPath)) return new GlobalSettings();
        try
        {
            var g = JsonSerializer.Deserialize<GlobalSettings>(
                File.ReadAllText(GlobalSettingsPath), JsonOptions) ?? new GlobalSettings();
            g.GlobalCertPassword = DecryptOrPassthrough(g.GlobalCertPassword);
            return g;
        }
        catch { return new GlobalSettings(); }
    }

    public void SaveGlobal()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GlobalSettingsPath) ?? ".");
        var storable = new GlobalSettings
        {
            RootFolder         = Global.RootFolder,
            CompanyName        = Global.CompanyName,
            UseGlobalCert      = Global.UseGlobalCert,
            GlobalCertPath     = Global.GlobalCertPath,
            GlobalCertPassword = DpapiService.Protect(Global.GlobalCertPassword),
            UseStoreCert       = Global.UseStoreCert,
            StoreCertThumbprint = Global.StoreCertThumbprint,
            KeepInCertStore    = Global.KeepInCertStore,
        };
        File.WriteAllText(GlobalSettingsPath, JsonSerializer.Serialize(storable, JsonOptions));
    }

    // Settings live at: {root}\apps\{AppName}\{AppName}-settings.json
    // StorageService.Update handles renaming this file when the app is renamed.

    public AppSettings LoadAppSettings(string appName)
    {
        var path = AppSettingsFilePath(appName);
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(path), JsonOptions) ?? new AppSettings();
            s.DefaultCertPassword = DecryptOrPassthrough(s.DefaultCertPassword);
            s.FtpPassword         = DecryptOrPassthrough(s.FtpPassword);
            s.FtpHost             = DecryptOrPassthrough(s.FtpHost);
            s.FtpUsername         = DecryptOrPassthrough(s.FtpUsername);
            s.FtpRemotePath       = DecryptOrPassthrough(s.FtpRemotePath);
            s.BaseDownloadUrl     = DecryptOrPassthrough(s.BaseDownloadUrl);
            return s;
        }
        catch { return new AppSettings(); }
    }

    public void SaveAppSettings(string appName, AppSettings settings)
    {
        var path = AppSettingsFilePath(appName);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var storable = new AppSettings
        {
            OutputFolder        = settings.OutputFolder,
            DefaultCertPath     = settings.DefaultCertPath,
            DefaultCertPassword = DpapiService.Protect(settings.DefaultCertPassword),
            PackageExtension    = settings.PackageExtension,
            FtpHost             = DpapiService.Protect(settings.FtpHost),
            FtpPort             = settings.FtpPort,
            FtpUsername         = DpapiService.Protect(settings.FtpUsername),
            FtpPassword         = DpapiService.Protect(settings.FtpPassword),
            FtpRemotePath       = DpapiService.Protect(settings.FtpRemotePath),
            BaseDownloadUrl     = DpapiService.Protect(settings.BaseDownloadUrl),
            UseStoreCert        = settings.UseStoreCert,
            StoreCertThumbprint = settings.StoreCertThumbprint,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(storable, JsonOptions));
    }

    // Decrypts a DPAPI blob; if the value is plain text (legacy), returns it as-is so
    // existing stored passwords survive the first load after the encryption upgrade.
    private static string? DecryptOrPassthrough(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return DpapiService.IsProtected(value) ? DpapiService.Unprotect(value) : value;
    }

    // Base folder for an app's output (version is appended at call sites)
    public string GetDefaultOutputBase(string appName)
        => Path.Combine(RootFolder, "releases", StorageService.Sanitize(appName));

    // Full versioned output directory, respecting any per-app folder override
    public string GetVersionOutputPath(string appName, string version, AppSettings? appSettings = null)
    {
        var baseDir = !string.IsNullOrWhiteSpace(appSettings?.OutputFolder)
            ? appSettings.OutputFolder!
            : GetDefaultOutputBase(appName);
        return Path.Combine(baseDir, version);
    }

    private string AppSettingsFilePath(string appName)
    {
        var sanitized = StorageService.Sanitize(appName);
        return Path.Combine(RootFolder, "apps", sanitized, $"{sanitized}-settings.json");
    }
}
