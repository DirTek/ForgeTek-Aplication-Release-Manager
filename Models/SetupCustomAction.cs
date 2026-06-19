namespace ForgeTekUpdatePackager.Models;

/// <summary>The kind of work a custom install action performs.</summary>
public enum CustomActionType
{
    /// <summary>Stop a Windows service by name (sc.exe stop).</summary>
    ServiceStop,
    /// <summary>Start a Windows service by name (sc.exe start).</summary>
    ServiceStart,
    /// <summary>Run a PowerShell script — an inline snippet or a bundled .ps1 file.</summary>
    RunPowerShell,
    /// <summary>Run a bundled (or absolute) executable with arguments.</summary>
    RunExecutable,
    /// <summary>Delete files/folders ([InstallDir] is resolved to the install root).</summary>
    DeleteFiles,
}

/// <summary>When an action runs relative to the file copy.</summary>
public enum CustomActionTiming { PreInstall, PostInstall }

/// <summary>
/// A deployment step the generated setup performs during a full install, beyond copying files.
/// Authored per bundle; executed (elevated) by the SetupBootstrapper.
/// </summary>
public class SetupCustomAction
{
    public CustomActionType Type { get; set; } = CustomActionType.RunPowerShell;
    public CustomActionTiming Timing { get; set; } = CustomActionTiming.PostInstall;

    /// <summary>Short description shown in the installer progress UI.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Service name | local exe/.ps1 path | file path or glob (depending on Type).</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Arguments passed to a RunExecutable target.</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>Inline PowerShell snippet for RunPowerShell when no .ps1 Target is set.</summary>
    public string InlineScript { get; set; } = string.Empty;

    /// <summary>Continue the install when this action fails (non-zero exit / exception).</summary>
    public bool IgnoreFailure { get; set; }

    /// <summary>Max seconds to wait for the action; 0 means wait indefinitely.</summary>
    public int TimeoutSeconds { get; set; }
}
