using System.IO;

namespace ForgeTekApplicationReleaseManager.Services;

/// <summary>
/// Machine-local working locations. In networked mode the shared <c>RootFolder</c>/<c>OutputFolder</c>
/// settings point at another operator's filesystem, so build output (the transient <c>.ftu</c>/manifest
/// that's uploaded then discarded) is written here instead — under this machine's local app data.
/// </summary>
public static class WorkspacePaths
{
    /// <summary>Root for per-machine build output, e.g. <c>%LOCALAPPDATA%\ForgeTek\work</c>.</summary>
    public static string LocalWorkRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ForgeTek", "work");
}
