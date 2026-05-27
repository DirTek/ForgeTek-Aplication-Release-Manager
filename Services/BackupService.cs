using System.IO;
using System.IO.Compression;

namespace ForgeTekUpdatePackager.Services;

public class BackupService : IBackupService
{
    public async Task CreateBackupAsync(
        string rootFolder,
        string globalSettingsFilePath,
        string outputPath,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var tmpPath = outputPath + ".tmp";
        try
        {
            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(tmpPath, ZipArchiveMode.Create);

                if (File.Exists(globalSettingsFilePath))
                {
                    zip.CreateEntryFromFile(globalSettingsFilePath, "settings/global.json");
                    progress.Report("  + settings/global.json");
                }

                AddFolder(zip, rootFolder, Path.Combine(rootFolder, "apps"), ct, progress);
                AddFolder(zip, rootFolder, Path.Combine(rootFolder, "releases"), ct, progress);

                var certDir = Path.Combine(rootFolder, "Certificates");
                if (Directory.Exists(certDir))
                    foreach (var file in Directory.GetFiles(certDir, "*.pfx").Order())
                    {
                        ct.ThrowIfCancellationRequested();
                        var rel = Path.GetRelativePath(rootFolder, file).Replace('\\', '/');
                        zip.CreateEntryFromFile(file, rel);
                        progress.Report($"  + {rel}");
                    }

                var logDir = Path.Combine(rootFolder, "logs");
                if (Directory.Exists(logDir))
                    foreach (var file in Directory.GetFiles(logDir, "*.log").Order())
                    {
                        ct.ThrowIfCancellationRequested();
                        var rel = Path.GetRelativePath(rootFolder, file).Replace('\\', '/');
                        zip.CreateEntryFromFile(file, rel);
                        progress.Report($"  + {rel}");
                    }
            }, ct);

            File.Move(tmpPath, outputPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    private static void AddFolder(ZipArchive zip, string rootFolder, string folder,
        CancellationToken ct, IProgress<string> progress)
    {
        if (!Directory.Exists(folder)) return;

        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order())
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(rootFolder, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, rel);
            progress.Report($"  + {rel}");
        }
    }
}
