using System.IO;
using System.Text.Json;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public class StorageService
{
    private static readonly string DataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ForgeTek", "UpdatePackager", "apps.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private List<AppEntry> _apps = [];

    public StorageService() => Load();

    private void Load()
    {
        if (!File.Exists(DataPath)) { _apps = []; return; }
        try
        {
            _apps = JsonSerializer.Deserialize<List<AppEntry>>(
                File.ReadAllText(DataPath), JsonOptions) ?? [];
        }
        catch { _apps = []; }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
        File.WriteAllText(DataPath, JsonSerializer.Serialize(_apps, JsonOptions));
    }

    public IReadOnlyList<AppEntry> GetAll() => _apps.AsReadOnly();
    public AppEntry? GetById(string id) => _apps.FirstOrDefault(a => a.Id == id);

    public void Add(AppEntry app) { _apps.Add(app); Save(); }

    public void Update(AppEntry app)
    {
        var idx = _apps.FindIndex(a => a.Id == app.Id);
        if (idx >= 0) _apps[idx] = app;
        Save();
    }

    public void Delete(string id) { _apps.RemoveAll(a => a.Id == id); Save(); }
}
