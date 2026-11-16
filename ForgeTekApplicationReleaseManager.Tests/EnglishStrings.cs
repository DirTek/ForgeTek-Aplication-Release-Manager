using System.IO;
using System.Xml.Linq;
using ForgeTekApplicationReleaseManager.Services;

namespace ForgeTekApplicationReleaseManager.Tests;

/// <summary>
/// <see cref="ILocalizationService"/> backed by the real Strings/en.xaml, read as plain XML (no WPF
/// Application needed), so view-model tests can assert on the English text users actually see.
/// </summary>
public sealed class EnglishStrings : ILocalizationService
{
    private static readonly Lazy<Dictionary<string, string>> Strings = new(Load);

    public string Current => "en";
    public event EventHandler? LanguageChanged { add { } remove { } }
    public void ApplySaved() { }
    public void Apply(string language) { }

    public string Get(string key, params object?[] args)
    {
        if (!Strings.Value.TryGetValue(key, out var value)) return key;
        return args is { Length: > 0 } ? string.Format(value, args) : value;
    }

    private static Dictionary<string, string> Load()
    {
        // Walk up from the test output folder to the repo, where Strings/en.xaml lives.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Strings", "en.xaml")))
            dir = dir.Parent;
        if (dir is null) throw new FileNotFoundException("Strings/en.xaml not found above the test output folder.");

        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(Path.Combine(dir.FullName, "Strings", "en.xaml")).Root!
            .Elements()
            .Where(e => e.Attribute(x + "Key") is not null)
            .ToDictionary(e => e.Attribute(x + "Key")!.Value, e => e.Value);
    }
}
