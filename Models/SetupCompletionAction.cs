namespace ForgeTekApplicationReleaseManager.Models;

/// <summary>What a finish-page action does when its checkbox is left checked.</summary>
public enum CompletionActionKind
{
    /// <summary>Open a URL in the default browser.</summary>
    OpenUrl,

    /// <summary>Open a file with its default app (e.g. a readme). [InstallDir] token allowed.</summary>
    OpenFile,
}

/// <summary>An optional action offered on the installer's final page as a toggleable checkbox.</summary>
public class SetupCompletionAction
{
    public CompletionActionKind Kind { get; set; }

    /// <summary>Checkbox text, e.g. "Visit our website".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>A URL (OpenUrl) or a file path (OpenFile). [InstallDir] is resolved at finish time.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Whether the checkbox starts checked on the final page.</summary>
    public bool DefaultChecked { get; set; } = true;
}
