using System.Windows;
using Microsoft.Win32;

namespace ForgeTekApplicationReleaseManager.Services;

/// <summary>
/// Swaps the active theme ResourceDictionary (Themes/Dark.xaml ↔ Themes/Light.xaml) merged into
/// Application.Resources. Because every theme brush is referenced via DynamicResource, switching
/// updates the whole UI live. The preference ("Dark" | "Light" | "System") is persisted in
/// GlobalSettings.Theme; "System" follows the OS app-theme and tracks live changes.
/// </summary>
public class ThemeService : IThemeService
{
    private readonly ISettingsService _settings;
    private bool _trackingOs;

    public ThemeService(ISettingsService settings) => _settings = settings;

    public string Current { get; private set; } = "Dark";

    public void ApplySaved() => Apply(_settings.Global.Theme, persist: false);

    public void Apply(string theme) => Apply(theme, persist: true);

    private void Apply(string preference, bool persist)
    {
        preference = Normalize(preference);
        Current = preference;

        ApplyEffective();
        TrackOs(preference == "System");

        if (persist && !string.Equals(_settings.Global.Theme, preference, StringComparison.Ordinal))
        {
            _settings.Global.Theme = preference;
            _settings.SaveGlobal();
        }
    }

    /// <summary>Resolves the preference to a concrete theme and swaps the merged dictionary.</summary>
    private void ApplyEffective()
    {
        var effective = Current == "System" ? DetectOsTheme() : Current;

        var merged = Application.Current.Resources.MergedDictionaries;
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            var src = merged[i].Source?.OriginalString;
            if (src is not null && src.Contains("Themes/", StringComparison.OrdinalIgnoreCase))
                merged.RemoveAt(i);
        }
        merged.Insert(0, new ResourceDictionary { Source = new Uri($"/Themes/{effective}.xaml", UriKind.Relative) });
    }

    private void TrackOs(bool track)
    {
        if (track && !_trackingOs)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _trackingOs = true;
        }
        else if (!track && _trackingOs)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _trackingOs = false;
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        // Fires on a non-UI thread; marshal the dictionary swap to the dispatcher.
        Application.Current?.Dispatcher.Invoke(ApplyEffective);
    }

    private static string Normalize(string theme) =>
        string.Equals(theme, "System", StringComparison.OrdinalIgnoreCase) ? "System"
        : string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light"
        : "Dark";

    /// <summary>Reads the OS app-theme: AppsUseLightTheme (0 = dark, 1 = light).</summary>
    private static string DetectOsTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
                return v == 0 ? "Dark" : "Light";
        }
        catch { /* registry unavailable — fall back to Dark */ }
        return "Dark";
    }
}
