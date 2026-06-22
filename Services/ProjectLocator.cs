using System.IO;

namespace ForgeTekApplicationReleaseManager.Services;

/// <summary>Resolves a scan target (.sln/.csproj) from a path that may be a file or a folder
/// (e.g. a local repo clone). Shared by the dependency-vulnerability and license scanners.</summary>
public static class ProjectLocator
{
    public static string? Resolve(string? projectOrFolder)
    {
        if (string.IsNullOrWhiteSpace(projectOrFolder)) return null;
        if (File.Exists(projectOrFolder)) return projectOrFolder;
        if (!Directory.Exists(projectOrFolder)) return null;

        var sln = Directory.GetFiles(projectOrFolder, "*.sln").FirstOrDefault();
        if (sln is not null) return sln;
        return Directory.GetFiles(projectOrFolder, "*.csproj").FirstOrDefault();
    }
}
