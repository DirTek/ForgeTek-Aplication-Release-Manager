namespace ForgeTekUpdatePackager.Models;

public class SetupBundle
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = string.Empty;
    public List<SetupAppRef> Apps { get; set; } = [];
    public List<RedistEntry> Redists { get; set; } = [];
    public bool SignOutput { get; set; }
    public string EulaText { get; set; } = string.Empty;
    public string? BannerImage { get; set; }
    /// <summary>Icon (.ico or .exe) baked into the generated setup EXE. Bundle-level, separate
    /// from each app's own icon. When null, falls back to the first app's icon.</summary>
    public string? SetupIconPath { get; set; }
    /// <summary>For multi-app bundles, which app the setup offers to launch on the final page
    /// (AppId). When null, no launch option is shown.</summary>
    public string? LaunchAppId { get; set; }
    /// <summary>The exe (within the launch app's files) the final page launches. When null, the
    /// first exe in that app's folder is used.</summary>
    public string? LaunchExeName { get; set; }
    public string? LastGeneratedPath { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime? LastGeneratedDate { get; set; }
}
