using System.IO;

namespace ForgeTekApplicationReleaseManager.Services;

/// <summary>
/// Extracts an application's icon to a standalone <c>.ico</c> file so it can be baked into
/// the setup bootstrapper at publish time via the <c>ApplicationIcon</c> MSBuild property.
///
/// IMPORTANT: We deliberately do NOT inject icons into the finished setup EXE using the Win32
/// <c>BeginUpdateResource</c>/<c>EndUpdateResource</c> APIs. The bootstrapper is a .NET
/// single-file, self-contained publish — the entire runtime is appended to the native apphost
/// as overlay data (~155 MB after a ~10 MB PE). Win32 resource updates rewrite the PE and
/// discard that overlay, truncating the bootstrapper to a ~10 MB stub and producing a broken
/// setup (the exact "missing ZIP payload" / 9 MB / 74 MB symptom). Setting the icon before
/// .NET bundles the runtime is the only safe way to brand a single-file executable.
/// </summary>
internal static class IconExtractor
{
    /// <summary>
    /// Extracts the icon associated with <paramref name="sourceExePath"/> and writes it to
    /// <paramref name="destIcoPath"/>. Returns <c>true</c> on success.
    /// </summary>
    public static bool TryExtractToIco(string sourceExePath, string destIcoPath)
    {
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(sourceExePath);
        if (icon is null)
            return false;

        using var fs = File.Create(destIcoPath);
        icon.Save(fs);
        return true;
    }
}
