namespace ForgeTekApplicationReleaseManager.Services;

/// <summary>
/// Swaps the active string ResourceDictionary (Strings/&lt;culture&gt;.xaml) merged into
/// Application.Resources, mirroring <see cref="IThemeService"/>. Because localized strings are
/// referenced via DynamicResource, switching updates the UI live. The preference is persisted in
/// GlobalSettings.Language. English ("en") is the default and currently the only language.
/// </summary>
public interface ILocalizationService
{
    /// <summary>Active culture code (e.g. "en").</summary>
    string Current { get; }

    /// <summary>Raised after the language has changed, so C#-computed labels can refresh.</summary>
    event EventHandler? LanguageChanged;

    /// <summary>Applies the saved language at startup (no persist).</summary>
    void ApplySaved();

    /// <summary>Switches language live and persists the preference.</summary>
    void Apply(string language);

    /// <summary>Resolves a string resource by key, formatting with <paramref name="args"/> when given.
    /// Returns the key itself if the resource is missing (so gaps are visible, never a crash).</summary>
    string Get(string key, params object[] args);
}
