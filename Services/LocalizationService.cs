using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace ForgeTekApplicationReleaseManager.Services;

/// <summary>
/// Swaps the active string ResourceDictionary (Strings/&lt;culture&gt;.xaml) merged into
/// Application.Resources. Built to mirror <see cref="ThemeService"/>: ApplySaved() at startup,
/// Apply(x) live + persist, dictionary swap by matching the Source substring. Because localized
/// strings are referenced via DynamicResource, switching updates the whole UI live.
/// </summary>
public class LocalizationService : ILocalizationService
{
    private readonly ISettingsService _settings;

    public LocalizationService(ISettingsService settings) => _settings = settings;

    public string Current { get; private set; } = "en";

    public event EventHandler? LanguageChanged;

    public void ApplySaved() => Apply(_settings.Global.Language, persist: false);

    public void Apply(string language) => Apply(language, persist: true);

    private void Apply(string language, bool persist)
    {
        language = Normalize(language);
        Current = language;

        SwapDictionary(language);
        ApplyCulture(language);

        if (persist && !string.Equals(_settings.Global.Language, language, StringComparison.Ordinal))
        {
            _settings.Global.Language = language;
            _settings.SaveGlobal();
        }

        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes any merged Strings/*.xaml dictionary and adds the chosen one.</summary>
    private static void SwapDictionary(string language)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            var src = merged[i].Source?.OriginalString;
            if (src is not null && src.Contains("Strings/", StringComparison.OrdinalIgnoreCase))
                merged.RemoveAt(i);
        }
        merged.Add(new ResourceDictionary { Source = new Uri($"/Strings/{language}.xaml", UriKind.Relative) });
    }

    /// <summary>Sets the thread/UI culture so date/number StringFormat bindings render per-locale.</summary>
    private static void ApplyCulture(string language)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            CultureInfo.DefaultThreadCurrentCulture   = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture       = culture;
            Thread.CurrentThread.CurrentUICulture     = culture;
            // Updates already-realized bindings' language; MainWindow may not exist yet at startup.
            if (Application.Current?.MainWindow is { } window)
                window.Language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);
        }
        catch { /* unknown culture code — keep the current culture */ }
    }

    public string Get(string key, params object[] args)
    {
        var value = Application.Current?.TryFindResource(key) as string ?? key;
        return args is { Length: > 0 } ? string.Format(value, args) : value;
    }

    private static string Normalize(string? language)
        => string.IsNullOrWhiteSpace(language) ? "en" : language.Trim();
}
