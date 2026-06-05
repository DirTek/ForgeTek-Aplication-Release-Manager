using System.IO;
using System.Text.Json;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public class SetupStorageService : ISetupStorageService
{
    private readonly string _setupsRoot;
    private readonly ILogService _log;
    private List<SetupBundle> _bundles = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public SetupStorageService(ISettingsService settings, ILogService log)
    {
        _setupsRoot = Path.Combine(settings.RootFolder, "setups");
        _log = log;
        Load();
    }

    private void Load()
    {
        _bundles = [];
        if (!Directory.Exists(_setupsRoot)) return;

        foreach (var file in Directory.GetFiles(_setupsRoot, "*.json"))
        {
            try
            {
                var bundle = JsonSerializer.Deserialize<SetupBundle>(
                    File.ReadAllText(file), JsonOptions);
                if (bundle is not null)
                    _bundles.Add(bundle);
            }
            catch (Exception ex)
            {
                _log.Write("SetupStorage", $"Failed to load {file}: {ex.Message}");
            }
        }
    }

    public IReadOnlyList<SetupBundle> GetAll() => _bundles.AsReadOnly();

    public SetupBundle? GetById(string id) => _bundles.FirstOrDefault(b => b.Id == id);

    public void Save(SetupBundle bundle)
    {
        var path = FilePath(bundle.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(bundle, JsonOptions));

        var idx = _bundles.FindIndex(b => b.Id == bundle.Id);
        if (idx >= 0) _bundles[idx] = bundle;
        else _bundles.Add(bundle);
    }

    public void Delete(string id)
    {
        var bundle = _bundles.FirstOrDefault(b => b.Id == id);
        if (bundle is null) return;

        var path = FilePath(bundle.Name);
        if (File.Exists(path))
            File.Delete(path);

        _bundles.Remove(bundle);
    }

    private string FilePath(string name)
    {
        var sanitized = StorageService.Sanitize(name);
        return Path.Combine(_setupsRoot, $"{sanitized}.json");
    }
}
