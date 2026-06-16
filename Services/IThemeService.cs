namespace ForgeTekUpdatePackager.Services;

public interface IThemeService
{
    /// <summary>Current preference: "Dark", "Light", or "System".</summary>
    string Current { get; }

    /// <summary>Applies the saved preference at startup (no re-persist).</summary>
    void ApplySaved();

    /// <summary>Switches preference ("Dark"/"Light"/"System"), swaps the dictionary, and persists.</summary>
    void Apply(string theme);
}
