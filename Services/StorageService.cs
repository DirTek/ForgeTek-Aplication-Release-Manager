using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public class StorageService : IStorageService
{
    private readonly string _appsRoot;
    private readonly ILogService _log;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    // Maps appId → sanitized folder name so renames can be detected
    private readonly Dictionary<string, string> _folderNames = new();
    private List<AppEntry> _apps = [];

    public StorageService(ISettingsService settings, ILogService log)
    {
        _appsRoot = Path.Combine(settings.RootFolder, "apps");
        _log = log;
        Load();
    }

    private void Load()
    {
        _apps = [];
        _folderNames.Clear();

        if (!Directory.Exists(_appsRoot)) return;

        foreach (var dir in Directory.GetDirectories(_appsRoot))
        {
            var folderName = Path.GetFileName(dir)!;
            var jsonFile   = Path.Combine(dir, $"{folderName}.json");
            if (!File.Exists(jsonFile)) continue;

            try
            {
                var app = JsonSerializer.Deserialize<AppEntry>(
                    File.ReadAllText(jsonFile), JsonOptions);
                if (app is null) continue;
                _apps.Add(app);
                _folderNames[app.Id] = folderName;

                foreach (var v in app.Versions)
                {
                    v.FtpHost     = DecryptOrPassthrough(v.FtpHost);
                    v.FtpUsername = DecryptOrPassthrough(v.FtpUsername);
                    v.FtpPassword = DecryptOrPassthrough(v.FtpPassword);
                }
            }
            catch (Exception ex) { _log.Write("Storage", $"Failed to load app from {folderName}: {ex.Message}"); }
        }
    }

    public IReadOnlyList<AppEntry> GetAll() => _apps.AsReadOnly();

    public AppEntry? GetById(string id) => _apps.FirstOrDefault(a => a.Id == id);

    public void Add(AppEntry app)
    {
        var folderName = Sanitize(app.Name);
        WriteAppFile(app, folderName);
        _apps.Add(app);
        _folderNames[app.Id] = folderName;
    }

    public void Update(AppEntry app)
    {
        var newFolder = Sanitize(app.Name);

        // Rename folder + all files starting with the old name when the app name changes
        if (_folderNames.TryGetValue(app.Id, out var oldFolder) &&
            !string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase))
        {
            var oldPath = Path.Combine(_appsRoot, oldFolder);
            var newPath = Path.Combine(_appsRoot, newFolder);

            if (Directory.Exists(oldPath))
            {
                Directory.Move(oldPath, newPath);

                foreach (var file in Directory.GetFiles(newPath))
                {
                    var fn = Path.GetFileName(file);
                    if (fn.StartsWith(oldFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        var newFn = newFolder + fn[oldFolder.Length..];
                        File.Move(file, Path.Combine(newPath, newFn));
                    }
                }
            }
        }

        WriteAppFile(app, newFolder);

        var idx = _apps.FindIndex(a => a.Id == app.Id);
        if (idx >= 0) _apps[idx] = app;
        else          _apps.Add(app);

        _folderNames[app.Id] = newFolder;
    }

    public void Delete(string id)
    {
        if (!_folderNames.TryGetValue(id, out var folderName)) return;

        var folderPath = Path.Combine(_appsRoot, folderName);
        if (Directory.Exists(folderPath))
            Directory.Delete(folderPath, recursive: true);

        _apps.RemoveAll(a => a.Id == id);
        _folderNames.Remove(id);
    }

    private void WriteAppFile(AppEntry app, string folderName)
    {
        // Serialize to a JSON DOM first, then encrypt credential fields in the DOM.
        // This avoids mutating the live in-memory AppEntry object, which could be
        // observed by the UI between the encrypt and restore steps.
        var root = JsonNode.Parse(JsonSerializer.Serialize(app, JsonOptions))?.AsObject()
                    ?? throw new InvalidOperationException("Failed to serialize app entry for storage");

        if (root["versions"]?.AsArray() is JsonArray versions)
        {
            foreach (var item in versions)
            {
                if (item is not JsonObject v) continue;
                EncryptField(v, "ftpHost");
                EncryptField(v, "ftpUsername");
                EncryptField(v, "ftpPassword");
            }
        }

        var folderPath = Path.Combine(_appsRoot, folderName);
        Directory.CreateDirectory(folderPath);
        File.WriteAllText(
            Path.Combine(folderPath, $"{folderName}.json"),
            root.ToJsonString(JsonOptions));
    }

    private static void EncryptField(JsonObject obj, string key)
    {
        if (obj[key] is JsonValue jv && jv.TryGetValue<string>(out var val) && !string.IsNullOrEmpty(val))
            obj[key] = DpapiService.Protect(val);
    }

    private static string? DecryptOrPassthrough(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return DpapiService.IsProtected(value) ? DpapiService.Unprotect(value) : value;
    }

    internal static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }
}
