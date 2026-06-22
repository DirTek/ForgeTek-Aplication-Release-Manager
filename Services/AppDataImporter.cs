using Microsoft.EntityFrameworkCore;
using ForgeTekUpdatePackager.Data;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services.Security;
using ForgeTekUpdatePackager.Services.Storage;

namespace ForgeTekUpdatePackager.Services;

/// <summary>
/// One-time migration of the legacy per-file JSON store into the database. Reads through the original
/// file-based services (decrypting secrets with DPAPI — only the original user/machine can do this) and
/// writes through the EF services (which re-encrypt with the active protector, or drop secrets when the
/// networked protector has no shared key yet). Idempotent: existing rows are left untouched.
/// </summary>
public sealed class AppDataImporter(
    IDbContextFactory<ForgeTekDbContext> factory,
    IStorageService efStorage,
    ISetupStorageService efSetup,
    ISettingsService efSettings,
    ILogService log)
{
    /// <summary>True when the DB has no apps and no users — a fresh store worth importing into.</summary>
    public bool NeedsImport()
    {
        using var db = factory.CreateDbContext();
        return !db.Apps.AsNoTracking().Any() && !db.Users.AsNoTracking().Any();
    }

    /// <summary>Copies all apps, per-app settings, setups + history, users (hashes preserved), and global
    /// settings from the JSON files into the DB. Returns the number of apps imported.</summary>
    public int ImportFromFiles()
    {
        var dpapi = new DpapiSecretProtector();
        var fileSettings = new SettingsService(dpapi);
        var fileStorage  = new StorageService(fileSettings, log, dpapi);
        var fileSetup    = new SetupStorageService(fileSettings, log, dpapi);
        var fileUsers    = new UserService();

        var count = 0;
        foreach (var app in fileStorage.GetAll())
        {
            if (efStorage.GetById(app.Id) is null) { efStorage.Add(app); count++; }
            efSettings.SaveAppSettings(app.Name, fileSettings.LoadAppSettings(app.Name));
        }

        foreach (var bundle in fileSetup.GetAll())
            efSetup.Save(bundle);

        var existingHistory = efSetup.GetHistory().Select(h => h.Id).ToHashSet();
        foreach (var record in fileSetup.GetHistory())
            if (!existingHistory.Contains(record.Id))
                efSetup.AddHistory(record);

        ImportUsers(fileUsers.GetAll());

        CopyProps(fileSettings.Global, efSettings.Global);
        efSettings.SaveGlobal();

        log.Write("Import", $"Imported {count} app(s), {fileSetup.GetAll().Count} setup(s), " +
                            $"{fileUsers.GetAll().Count} user(s) from JSON into the database.");
        return count;
    }

    // Users carry pre-computed PBKDF2 hashes, so insert the rows directly (the EF service's Create() would
    // re-hash a new plaintext password instead).
    private void ImportUsers(IReadOnlyList<AppUser> users)
    {
        using var db = factory.CreateDbContext();
        foreach (var user in users)
        {
            var key = user.Username.Trim().ToLowerInvariant();
            if (db.Users.Any(u => u.UsernameKey == key)) continue;
            db.Users.Add(new UserRow { UsernameKey = key, Payload = EfJson.Serialize(user) });
        }
        db.SaveChanges();
    }

    // Shallow-copies all public read/write properties (GlobalSettings is a flat settings bag).
    private static void CopyProps<T>(T source, T target)
    {
        foreach (var p in typeof(T).GetProperties())
            if (p is { CanRead: true, CanWrite: true })
                p.SetValue(target, p.GetValue(source));
    }
}
