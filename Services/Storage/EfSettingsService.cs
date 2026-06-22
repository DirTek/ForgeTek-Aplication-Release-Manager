using System.IO;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ForgeTekApplicationReleaseManager.Data;
using ForgeTekApplicationReleaseManager.Models;
using ForgeTekApplicationReleaseManager.Services.Security;

namespace ForgeTekApplicationReleaseManager.Services.Storage;

/// <summary>
/// EF Core-backed settings store. <see cref="Global"/> is the single shared global-settings row, cached in
/// memory and written back on <see cref="SaveGlobal"/>. Per-app settings are one row each (keyed by app
/// name, matching the old file layout). Secret fields are encrypted in the JSON payload via the protector
/// (or dropped when the networked protector has no shared key yet).
/// </summary>
public sealed class EfSettingsService : ISettingsService
{
    private static readonly string[] GlobalSecretKeys = ["globalCertPassword", "gitHubToken"];

    private static readonly string[] AppSecretKeys =
    [
        "defaultCertPassword", "ftpHost", "ftpUsername", "ftpPassword", "ftpRemotePath",
        "baseDownloadUrl", "sftpPassword", "s3SecretKey", "gitHubToken",
    ];

    private readonly IDbContextFactory<ForgeTekDbContext> _factory;
    private readonly ISecretProtector _protector;
    private readonly bool _networked;

    public GlobalSettings Global { get; }
    public string RootFolder => Global.RootFolder;

    public EfSettingsService(IDbContextFactory<ForgeTekDbContext> factory, ISecretProtector protector)
    {
        _factory = factory;
        _protector = protector;
        // Networked: the shared RootFolder/OutputFolder belong to another machine, so build output is
        // redirected to a local working dir (see GetDefaultOutputBase).
        var c = Config.ConnectionConfig.Load();
        _networked = c.IsNetworked && !string.IsNullOrWhiteSpace(c.SqlServerConnectionString);
        Global = LoadGlobal();
    }

    private GlobalSettings LoadGlobal()
    {
        using var db = _factory.CreateDbContext();
        var row = db.GlobalSettingsRows.AsNoTracking().FirstOrDefault(g => g.Id == GlobalSettingsRow.SingletonId);
        if (row is null) return new GlobalSettings();
        var obj = JsonNode.Parse(row.Payload)!.AsObject();
        foreach (var key in GlobalSecretKeys) EfJson.Decrypt(obj, key, _protector);
        return EfJson.Deserialize<GlobalSettings>(obj.ToJsonString(EfJson.Options));
    }

    public void SaveGlobal()
    {
        var obj = JsonNode.Parse(EfJson.Serialize(Global))!.AsObject();
        foreach (var key in GlobalSecretKeys) EfJson.ProtectOrDrop(obj, key, _protector);
        var payload = obj.ToJsonString(EfJson.Options);

        using var db = _factory.CreateDbContext();
        var row = db.GlobalSettingsRows.FirstOrDefault(g => g.Id == GlobalSettingsRow.SingletonId);
        if (row is null)
            db.GlobalSettingsRows.Add(new GlobalSettingsRow { Payload = payload });
        else
            row.Payload = payload;
        db.SaveChanges();
    }

    public AppSettings LoadAppSettings(string appName)
    {
        using var db = _factory.CreateDbContext();
        var row = db.AppSettingsRows.AsNoTracking().FirstOrDefault(s => s.AppName == appName);
        if (row is null) return new AppSettings();
        var obj = JsonNode.Parse(row.Payload)!.AsObject();
        foreach (var key in AppSecretKeys) EfJson.Decrypt(obj, key, _protector);
        return EfJson.Deserialize<AppSettings>(obj.ToJsonString(EfJson.Options));
    }

    public void SaveAppSettings(string appName, AppSettings settings)
    {
        var obj = JsonNode.Parse(EfJson.Serialize(settings))!.AsObject();
        foreach (var key in AppSecretKeys) EfJson.ProtectOrDrop(obj, key, _protector);
        var payload = obj.ToJsonString(EfJson.Options);

        using var db = _factory.CreateDbContext();
        var row = db.AppSettingsRows.FirstOrDefault(s => s.AppName == appName);
        if (row is null)
            db.AppSettingsRows.Add(new AppSettingsRow { AppName = appName, Payload = payload });
        else
            row.Payload = payload;
        db.SaveChanges();
    }

    // Output-path helpers: standalone uses the shared RootFolder like the file-based service. Networked
    // writes to a per-machine working dir, since RootFolder/OutputFolder are another operator's paths and
    // the built artifact is transient (uploaded then discarded).
    public string GetDefaultOutputBase(string appName)
        => _networked
            ? Path.Combine(WorkspacePaths.LocalWorkRoot, StorageService.Sanitize(appName))
            : Path.Combine(RootFolder, "releases", StorageService.Sanitize(appName));

    public string GetVersionOutputPath(string appName, string version, AppSettings? appSettings = null)
    {
        // In networked mode ignore the (foreign) per-app OutputFolder and use the local working dir.
        var baseDir = !_networked && !string.IsNullOrWhiteSpace(appSettings?.OutputFolder)
            ? appSettings.OutputFolder!
            : GetDefaultOutputBase(appName);
        return Path.Combine(baseDir, version);
    }
}
