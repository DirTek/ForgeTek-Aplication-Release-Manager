using System.IO;
using System.Text.Json;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services.Security;

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

    private readonly ISecretProtector _protector;

    public GlobalSettings Global { get; }
    public string RootFolder => Global.RootFolder;

    public SettingsService(ISecretProtector protector)
    {
        _protector = protector;
        Global = LoadGlobal();
    }

    private GlobalSettings LoadGlobal()
    {
        if (!File.Exists(GlobalSettingsPath)) return new GlobalSettings();
        try
        {
            var g = JsonSerializer.Deserialize<GlobalSettings>(
                File.ReadAllText(GlobalSettingsPath), JsonOptions) ?? new GlobalSettings();
            g.GlobalCertPassword = DecryptOrPassthrough(g.GlobalCertPassword);
            g.GitHubToken        = DecryptOrPassthrough(g.GitHubToken);
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
            GlobalCertPassword = _protector.Protect(Global.GlobalCertPassword),
            UseStoreCert       = Global.UseStoreCert,
            StoreCertThumbprint = Global.StoreCertThumbprint,
            KeepInCertStore    = Global.KeepInCertStore,
            Theme              = Global.Theme,
            VersionChannelFilter = Global.VersionChannelFilter,
            RequireReleaseApproval = Global.RequireReleaseApproval,
            PublisherUrl        = Global.PublisherUrl,
            PublisherSupportUrl = Global.PublisherSupportUrl,
            Vulnerability       = Global.Vulnerability,
            License             = Global.License,
            GitHubClientId     = Global.GitHubClientId,
            GitHubToken        = _protector.Protect(Global.GitHubToken),
            GitHubLogin        = Global.GitHubLogin,
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
            s.GitHubToken         = DecryptOrPassthrough(s.GitHubToken);
            s.SftpPassword        = DecryptOrPassthrough(s.SftpPassword);
            s.S3SecretKey         = DecryptOrPassthrough(s.S3SecretKey);
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
            DefaultCertPassword = _protector.Protect(settings.DefaultCertPassword),
            PackageExtension    = settings.PackageExtension,
            PackageNameTemplate = settings.PackageNameTemplate,
            PublishProvider     = settings.PublishProvider,
            FtpHost             = _protector.Protect(settings.FtpHost),
            FtpPort             = settings.FtpPort,
            FtpUsername         = _protector.Protect(settings.FtpUsername),
            FtpPassword         = _protector.Protect(settings.FtpPassword),
            FtpRemotePath       = _protector.Protect(settings.FtpRemotePath),
            BaseDownloadUrl     = _protector.Protect(settings.BaseDownloadUrl),
            SftpHost            = settings.SftpHost,
            SftpPort            = settings.SftpPort,
            SftpUsername        = settings.SftpUsername,
            SftpPassword        = _protector.Protect(settings.SftpPassword),
            SftpRemotePath      = settings.SftpRemotePath,
            SftpBaseDownloadUrl = settings.SftpBaseDownloadUrl,
            S3Endpoint          = settings.S3Endpoint,
            S3Region            = settings.S3Region,
            S3Bucket            = settings.S3Bucket,
            S3AccessKey         = settings.S3AccessKey,
            S3SecretKey         = _protector.Protect(settings.S3SecretKey),
            S3Prefix            = settings.S3Prefix,
            S3PublicBaseUrl     = settings.S3PublicBaseUrl,
            GitHubReleaseTag    = settings.GitHubReleaseTag,
            GitHubCatalogTag    = settings.GitHubCatalogTag,
            UseStoreCert        = settings.UseStoreCert,
            StoreCertThumbprint = settings.StoreCertThumbprint,
            GitHubRepo          = settings.GitHubRepo,
            GitHubToken         = _protector.Protect(settings.GitHubToken),
            GitHubLocalPath     = settings.GitHubLocalPath,
            GitHubBuildCommand  = settings.GitHubBuildCommand,
            GitHubArtifactPath  = settings.GitHubArtifactPath,
            Winget              = settings.Winget,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(storable, JsonOptions));
    }

    // Decrypts a protected blob; if the value is plain text (legacy), returns it as-is so
    // existing stored passwords survive the first load after the encryption upgrade.
    private string? DecryptOrPassthrough(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return _protector.IsProtected(value) ? _protector.Unprotect(value) : value;
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
