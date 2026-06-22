using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services;

public interface IScannerService
{
    IReadOnlyList<FileRecord> ScanDirectory(string folderPath, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);
    string ComputeChecksum(string path);
    string? DetectExeVersion(string folderPath, string appName);
    string? ReadExeVersion(string fullPath);
    IReadOnlyList<string> FindRootExeFiles(string folderPath);
    DiffResult DiffVersions(AppVersion baseVersion, IReadOnlyList<FileRecord> newFiles, IProgress<DiffProgress>? progress = null);
}
