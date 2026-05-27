using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public interface ISigningService
{
    string? FindSignTool();
    IReadOnlyList<SignableFile> GetSignableFiles(IEnumerable<FileRecord> files, string rootPath, AppVersion? baseVersion = null);
    Task SignFilesAsync(string signToolPath, IReadOnlyList<string> filePaths,
        string? pfxPath, string? password, string? storeThumbprint,
        IProgress<string> progress, CancellationToken ct = default);
}
