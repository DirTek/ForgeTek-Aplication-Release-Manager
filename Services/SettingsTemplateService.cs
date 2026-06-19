using System.IO;
using System.Linq;
using System.Text.Json;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public class SettingsTemplateService : ISettingsTemplateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ISettingsService _settings;

    public SettingsTemplateService(ISettingsService settings) => _settings = settings;

    private string TemplatesPath => Path.Combine(_settings.RootFolder, "templates.json");

    public IReadOnlyList<SettingsTemplate> GetAll()
    {
        var list = new List<SettingsTemplate>(BuiltIns());
        list.AddRange(LoadUser());
        return list;
    }

    public void Save(SettingsTemplate template)
    {
        if (template.IsBuiltIn) return;

        var user = LoadUser();
        var idx = user.FindIndex(t => t.Id == template.Id);
        if (idx >= 0) user[idx] = template;
        else user.Add(template);
        Persist(user);
    }

    public void Delete(string id)
    {
        var user = LoadUser();
        if (user.RemoveAll(t => t.Id == id) > 0)
            Persist(user);
    }

    private List<SettingsTemplate> LoadUser()
    {
        if (!File.Exists(TemplatesPath)) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<SettingsTemplate>>(
                File.ReadAllText(TemplatesPath), JsonOptions) ?? [];
            foreach (var t in list) t.IsBuiltIn = false;
            return list;
        }
        catch { return []; }
    }

    private void Persist(List<SettingsTemplate> user)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TemplatesPath) ?? ".");
        File.WriteAllText(TemplatesPath, JsonSerializer.Serialize(user, JsonOptions));
    }

    // Shipped starting points. The stack mostly determines the build command + artifact folder.
    private static IEnumerable<SettingsTemplate> BuiltIns()
    {
        const string dotnetPublish = "dotnet publish -c Release -o publish";

        SettingsTemplate Preset(string name, string build, string artifact) => new()
        {
            Id = "builtin:" + name,
            Name = name,
            IsBuiltIn = true,
            PackageExtension = "ftu",
            PackageNameTemplate = "{AppName}-{Version}",
            GitHubBuildCommand = build,
            GitHubArtifactPath = artifact,
        };

        yield return Preset("WPF (.NET)", dotnetPublish, "publish");
        yield return Preset("WinForms (.NET)", dotnetPublish, "publish");
        yield return Preset("Console (.NET)", dotnetPublish, "publish");
        yield return Preset("Windows Service (.NET)", dotnetPublish, "publish");
        yield return Preset(".NET MAUI (Windows)",
            "dotnet publish -f net9.0-windows10.0.19041.0 -c Release", "bin\\Release");
    }
}
