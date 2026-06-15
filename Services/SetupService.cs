using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public class SetupService : ISetupService
{
    private static readonly byte[] EndMagic = "STUPEND"u8.ToArray();

    private static readonly HashSet<string> DebugExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdb", ".ilk", ".exp",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IStorageService _storage;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly ISigningService _signing;

    public SetupService(IStorageService storage, ISettingsService settings, ILogService log,
        ISigningService signing)
    {
        _storage = storage;
        _settings = settings;
        _log = log;
        _signing = signing;
    }

    public async Task<string> GenerateAsync(SetupBundle bundle, IProgress<SetupProgressInfo> progress, CancellationToken ct = default)
    {
        // 1. Locate the pre-built bootstrapper (used as-is when no custom icon is needed,
        //    and as a fallback if an on-demand branded publish fails).
        var prebuiltBootstrapper = LocateBootstrapper();
        if (prebuiltBootstrapper is null && LocateBootstrapperCsproj() is null)
            throw new FileNotFoundException(
                "SetupBootstrapper.exe not found. Build the solution first to compile the bootstrapper.");

        progress.Report(new SetupProgressInfo(5, "Bootstrapper found."));

        // 2. Collect files from selected apps
        var stagingDir = Path.Combine(Path.GetTempPath(), "ForgeTekSetupGen",
            Guid.NewGuid().ToString("N"));
        var zipDir = Path.Combine(Path.GetTempPath(), "ForgeTekSetupZip");
        var iconTempDir = Path.Combine(Path.GetTempPath(), "ForgeTekSetupIcon",
            Guid.NewGuid().ToString("N"));
        var publishDir = Path.Combine(Path.GetTempPath(), "ForgeTekBootstrapper",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(zipDir);

        try
        {
            var manifestApps = new List<InstallAppManifest>();
            var appCount = bundle.Apps.Count;
            var appIndex = 0;

            foreach (var appRef in bundle.Apps)
            {
                ct.ThrowIfCancellationRequested();

                var appEntry = _storage.GetById(appRef.AppId);
                if (appEntry is null)
                {
                    progress.Report(new SetupProgressInfo(10 + 30 * appIndex / Math.Max(appCount, 1),
                        $"⚠ App not found: {appRef.AppId} — skipped"));
                    continue;
                }

                progress.Report(new SetupProgressInfo(10 + 30 * appIndex / Math.Max(appCount, 1),
                    $"Collecting files for: {appEntry.Name}"));

                var files = CollectAppFiles(appEntry, appRef.VersionMode);

                if (files.Count == 0)
                {
                    progress.Report(new SetupProgressInfo(10 + 30 * appIndex / Math.Max(appCount, 1),
                        $"⚠ No files collected for {appEntry.Name} — skipped"));
                    continue;
                }

                var appDirName = StorageService.Sanitize(appEntry.Name);
                var appStagingDir = Path.Combine(stagingDir, "apps", appDirName);
                Directory.CreateDirectory(appStagingDir);

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();

                    var sourcePath = Path.Combine(appEntry.FolderPath, file.Path);
                    var targetPath = Path.Combine(appStagingDir, file.Path);

                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                    await using var src = File.OpenRead(sourcePath);
                    await using var dst = File.Create(targetPath);
                    await src.CopyToAsync(dst, ct);
                }

                manifestApps.Add(new InstallAppManifest
                {
                    Name = appEntry.Name,
                    DefaultInstallDir = appDirName,
                    LaunchExeName = appRef.LaunchExeName,
                    IconFileName = appRef.SetupIconPath,
                    CreateShortcut = appRef.CreateShortcut,
                    RunAsAdminExes = appRef.RunAsAdminExes,
                    RegistryEntries = appRef.RegistryEntries.Select(r => new RegistryEntryManifestInternal
                    {
                        Root = r.Root,
                        KeyPath = r.KeyPath,
                        ValueName = r.ValueName,
                        ValueData = r.ValueData,
                        ValueKind = r.ValueKind,
                    }).ToList(),
                });

                appIndex++;
                progress.Report(new SetupProgressInfo(10 + 30 * appIndex / Math.Max(appCount, 1),
                    $"✓ {files.Count} file(s) collected for {appEntry.Name}"));
            }

            if (manifestApps.Count == 0)
                throw new InvalidOperationException("No apps selected or no apps have files.");

            // 2b. Copy redistributable files
            var redistManifests = new List<InstallRedistManifest>();
            var redistIndex = 0;
            var redistCount = bundle.Redists.Count;

            foreach (var redist in bundle.Redists)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(redist.SourcePath) || !File.Exists(redist.SourcePath))
                {
                    progress.Report(new SetupProgressInfo(45 + 10 * redistIndex / Math.Max(redistCount, 1),
                        $"⚠ Redist not found: {redist.Name} — skipped"));
                    continue;
                }

                var redistDir = Path.Combine(stagingDir, "redist");
                Directory.CreateDirectory(redistDir);

                var exeName = Path.GetFileName(redist.SourcePath);
                var targetPath = Path.Combine(redistDir, exeName);
                await using (var src = File.OpenRead(redist.SourcePath))
                await using (var dst = File.Create(targetPath))
                    await src.CopyToAsync(dst, ct);

                redistManifests.Add(new InstallRedistManifest
                {
                    Name = redist.Name,
                    ExeName = exeName,
                    Arguments = redist.Arguments,
                    DetectionKeyPath = redist.DetectionKeyPath,
                    DetectionValueName = redist.DetectionValueName,
                    DetectionExpectedValue = redist.DetectionExpectedValue,
                });

                redistIndex++;
                progress.Report(new SetupProgressInfo(45 + 10 * redistIndex / Math.Max(redistCount, 1),
                    $"✓ Redist added: {redist.Name}"));
            }

            // 2b. Copy banner image if provided
            string? bannerName = null;
            if (!string.IsNullOrWhiteSpace(bundle.BannerImage) && File.Exists(bundle.BannerImage))
            {
                bannerName = "banner" + Path.GetExtension(bundle.BannerImage);
                var bannerTarget = Path.Combine(stagingDir, bannerName);
                File.Copy(bundle.BannerImage, bannerTarget, overwrite: true);
                progress.Report(new SetupProgressInfo(55, "✓ Banner image added."));
            }

            // 2c. Copy background image if the appearance uses one
            string? backgroundName = null;
            if (bundle.BackgroundMode == "Image"
                && !string.IsNullOrWhiteSpace(bundle.BackgroundImage) && File.Exists(bundle.BackgroundImage))
            {
                backgroundName = "background" + Path.GetExtension(bundle.BackgroundImage);
                File.Copy(bundle.BackgroundImage, Path.Combine(stagingDir, backgroundName), overwrite: true);
                progress.Report(new SetupProgressInfo(56, "✓ Background image added."));
            }

            // 3. Create install.json
            progress.Report(new SetupProgressInfo(60, "Creating install manifest…"));

            // Resolve the bundle's chosen launch app (offered on the setup's final page).
            InstallAppManifest? launchApp = null;
            if (!string.IsNullOrWhiteSpace(bundle.LaunchAppId))
            {
                var launchRef = bundle.Apps.FirstOrDefault(a => a.AppId == bundle.LaunchAppId);
                var launchEntry = launchRef is null ? null : _storage.GetById(launchRef.AppId);
                if (launchEntry is not null)
                {
                    var dir = StorageService.Sanitize(launchEntry.Name);
                    launchApp = manifestApps.FirstOrDefault(m => m.DefaultInstallDir == dir);
                }
            }

            var installManifest = new InstallManifest
            {
                SetupName = bundle.Name,
                SetupVersion = string.IsNullOrWhiteSpace(bundle.Version)
                    ? DateTime.Now.ToString("yyyy.MMdd.HHmm") : bundle.Version,
                CompanyName = _settings.Global.CompanyName,
                EulaText = bundle.EulaText,
                BannerImageName = bannerName,
                LaunchAppName = launchApp?.Name,
                LaunchAppDir = launchApp?.DefaultInstallDir,
                LaunchExeName = launchApp is not null ? bundle.LaunchExeName : null,
                BackgroundMode = bundle.BackgroundMode,
                BackgroundColor1 = bundle.BackgroundColor1,
                BackgroundColor2 = bundle.BackgroundColor2,
                BackgroundGradientDirection = bundle.BackgroundGradientDirection,
                BackgroundImageName = backgroundName,
                FixedSize = bundle.FixedSize,
                Apps = manifestApps,
                Redists = redistManifests,
            };

            var manifestJson = JsonSerializer.Serialize(installManifest, JsonOptions);
            await File.WriteAllTextAsync(Path.Combine(stagingDir, "install.json"), manifestJson, ct);

            // 3b. Bundle the small per-app uninstaller (AOT) if it has been built. The bootstrapper
            //     copies it into each app folder and injects that app's icon. If absent (AOT tools
            //     not installed yet), the bootstrapper falls back to a single shared uninstaller.
            var uninstallerPath = LocateUninstaller();
            if (uninstallerPath is not null)
            {
                var uninstStageDir = Path.Combine(stagingDir, "__uninstaller__");
                Directory.CreateDirectory(uninstStageDir);
                File.Copy(uninstallerPath, Path.Combine(uninstStageDir, "SetupUninstaller.exe"), overwrite: true);
                progress.Report(new SetupProgressInfo(68, "✓ Per-app uninstaller bundled."));
            }

            // 4. Create ZIP of staging directory
            progress.Report(new SetupProgressInfo(70, "Creating package archive…"));

            var zipPath = Path.Combine(zipDir, "package.zip");
            ZipFile.CreateFromDirectory(stagingDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

            progress.Report(new SetupProgressInfo(85, $"ZIP created: {new FileInfo(zipPath).Length / 1024.0:F1} KB"));

            // 5. Build the final setup executable (obtain bootstrapper → append ZIP → sign)
            progress.Report(new SetupProgressInfo(90, "Building setup executable…"));

            Directory.CreateDirectory(bundle.OutputFolder);
            var setupPath = Path.Combine(bundle.OutputFolder, $"{StorageService.Sanitize(bundle.Name)}Setup.exe");

            // 5a. Resolve the setup icon (first app's icon) to a .ico, if available.
            //     The icon is baked into the bootstrapper at publish time (ApplicationIcon)
            //     rather than injected into the finished EXE — Win32 resource updates strip
            //     the .NET single-file overlay and destroy the bootstrapper. See IconExtractor.
            var setupIcoPath = TryResolveSetupIcon(bundle, iconTempDir);

            // 5b. Obtain the bootstrapper. With an icon, publish a fresh single-file bootstrapper
            //     with that icon baked in; otherwise use the pre-built (unbranded) one.
            string? bootstrapperPath = null;
            if (setupIcoPath is not null)
            {
                progress.Report(new SetupProgressInfo(91, "Building branded bootstrapper…"));
                bootstrapperPath = await PublishBootstrapperWithIconAsync(setupIcoPath, publishDir, ct);
            }

            bootstrapperPath ??= prebuiltBootstrapper
                ?? throw new FileNotFoundException(
                    "SetupBootstrapper.exe could not be built or located.");

            // 5c. Copy bootstrapper (async stream to avoid any File.Copy attribute/lock issues)
            await using (var src = File.OpenRead(bootstrapperPath))
            await using (var dst = File.Create(setupPath))
            {
                await src.CopyToAsync(dst, ct);
            }

            // 5d. Append ZIP + footer (offset = bootstrapper size before the appended payload)
            var bootstrapperLength = new FileInfo(setupPath).Length;
            await AppendZipToExeAsync(zipPath, setupPath, bootstrapperLength, ct);

            // Verify footer was written correctly
            using (var verifyFs = new FileStream(setupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (verifyFs.Length < EndMagic.Length + 8)
                    throw new InvalidOperationException(
                        $"Setup file is too small ({verifyFs.Length} bytes) to contain the ZIP footer.");
                verifyFs.Seek(-EndMagic.Length, SeekOrigin.End);
                var magicBuf = new byte[EndMagic.Length];
                verifyFs.ReadExactly(magicBuf);
                if (!magicBuf.AsSpan().SequenceEqual(EndMagic))
                    throw new InvalidOperationException(
                        $"Setup file footer is corrupt or missing. " +
                        $"Expected {BitConverter.ToString(EndMagic)}, got {BitConverter.ToString(magicBuf)}. " +
                        $"File: {setupPath} ({new FileInfo(setupPath).Length} bytes)");
            }

            // 6. Sign the setup EXE if requested and configured (happens last so signature is preserved)
            var global = _settings.Global;
            var signToolPath = bundle.SignOutput && (global.UseStoreCert || !string.IsNullOrWhiteSpace(global.GlobalCertPath))
                ? _signing.FindSignTool() : null;

            if (signToolPath is not null)
            {
                progress.Report(new SetupProgressInfo(96, "Signing setup executable…"));
                var silentProgress = new Progress<string>();
                await _signing.SignFilesAsync(signToolPath,
                    [setupPath],
                    global.UseStoreCert ? null : global.GlobalCertPath,
                    global.UseStoreCert ? null : global.GlobalCertPassword,
                    global.UseStoreCert ? global.StoreCertThumbprint : null,
                    silentProgress, ct);
            }

            progress.Report(new SetupProgressInfo(100, $"✔ Setup generated → {setupPath}"));

            return setupPath;
        }
        finally
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); }
            catch { }
            try { if (Directory.Exists(zipDir)) Directory.Delete(zipDir, recursive: true); }
            catch { }
            try { if (Directory.Exists(iconTempDir)) Directory.Delete(iconTempDir, recursive: true); }
            catch { }
            try { if (Directory.Exists(publishDir)) Directory.Delete(publishDir, recursive: true); }
            catch { }
        }
    }

    /// <summary>
    /// Resolves the icon baked into the setup EXE to a standalone <c>.ico</c> (extracting from an
    /// EXE if needed). Prefers the bundle-level <see cref="SetupBundle.SetupIconPath"/>; otherwise
    /// falls back to the first app's icon. Returns the path, or <c>null</c> when none is available.
    /// </summary>
    private string? TryResolveSetupIcon(SetupBundle bundle, string iconTempDir)
    {
        string? srcPath = null;

        // 1. Bundle-level setup icon (its own card) — an absolute file path the user chose.
        if (!string.IsNullOrWhiteSpace(bundle.SetupIconPath) && File.Exists(bundle.SetupIconPath))
            srcPath = bundle.SetupIconPath;

        // 2. Fallback: the first app's chosen icon / launch exe (within the app's files).
        if (srcPath is null && bundle.Apps.Count > 0)
        {
            var firstAppRef = bundle.Apps[0];
            var firstAppEntry = _storage.GetById(firstAppRef.AppId);
            if (firstAppEntry is not null)
            {
                if (!string.IsNullOrWhiteSpace(firstAppRef.SetupIconPath))
                    srcPath = FindLaunchExeOnDisk(firstAppEntry, firstAppRef.SetupIconPath);
                if (srcPath is null && firstAppRef.LaunchExeName is not null)
                    srcPath = FindLaunchExeOnDisk(firstAppEntry, firstAppRef.LaunchExeName);
            }
        }

        if (srcPath is null || !File.Exists(srcPath))
            return null;

        // A real .ico is used directly (preserves all resolutions); anything else is extracted.
        if (".ico".Equals(Path.GetExtension(srcPath), StringComparison.OrdinalIgnoreCase))
            return srcPath;

        try
        {
            Directory.CreateDirectory(iconTempDir);
            var ico = Path.Combine(iconTempDir, "setup.ico");
            if (IconExtractor.TryExtractToIco(srcPath, ico))
                return ico;
        }
        catch (Exception ex)
        {
            _log.Write("Setup", $"Icon extraction failed for '{srcPath}': {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Publishes a fresh single-file, self-contained bootstrapper with <paramref name="icoPath"/>
    /// baked in as the application icon. Returns the published EXE path, or <c>null</c> on failure
    /// (caller falls back to the pre-built bootstrapper).
    /// </summary>
    private async Task<string?> PublishBootstrapperWithIconAsync(string icoPath, string publishDir, CancellationToken ct)
    {
        var csproj = LocateBootstrapperCsproj();
        if (csproj is null)
        {
            _log.Write("Setup", "Bootstrapper .csproj not found; cannot bake in custom icon.");
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(csproj),
        };
        psi.ArgumentList.Add("publish");
        psi.ArgumentList.Add(csproj);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(GetConfiguration());
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add("win-x64");
        psi.ArgumentList.Add("--self-contained");
        psi.ArgumentList.Add("true");
        psi.ArgumentList.Add("-p:PublishSingleFile=true");
        // Must mirror the pre-built bootstrapper (see PublishSetupBootstrapper target in the
        // main .csproj). Without IncludeNativeLibrariesForSelfExtract the self-contained WPF
        // single-file fails to load its native libraries and crashes before the window shows.
        psi.ArgumentList.Add("-p:IncludeNativeLibrariesForSelfExtract=true");
        psi.ArgumentList.Add($"-p:ApplicationIcon={icoPath}");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(publishDir);

        try
        {
            using var proc = new Process { StartInfo = psi };
            var output = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
            {
                _log.Write("Setup",
                    $"Branded bootstrapper publish failed (exit {proc.ExitCode}); using pre-built bootstrapper.\n{output}");
                return null;
            }
        }
        catch (Exception ex)
        {
            _log.Write("Setup", $"Branded bootstrapper publish errored: {ex.Message}");
            return null;
        }

        var exe = Path.Combine(publishDir, "SetupBootstrapper.exe");
        return File.Exists(exe) ? exe : null;
    }

    private static List<FileRecord> CollectAppFiles(AppEntry entry, VersionMode mode)
    {
        var versions = entry.Versions
            .Where(v => v.Status != VersionStatus.Retracted && v.Status != VersionStatus.Scrapped)
            .ToList();

        if (versions.Count == 0) return [];

        IEnumerable<FileRecord> Filter(IEnumerable<FileRecord> files)
            => files.Where(f => !f.IsDebug && !DebugExtensions.Contains(Path.GetExtension(f.Path)));

        if (mode == VersionMode.LatestOnly)
        {
            var latest = versions.Last();
            return Filter(latest.Files).ToList();
        }

        // Cumulative: merge all versions, latest file wins
        var fileMap = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var version in versions)
        {
            foreach (var file in Filter(version.Files))
            {
                // Last version's file wins for any given path
                fileMap[file.Path] = file;
            }
        }

        return fileMap.Values.ToList();
    }

    private static async Task AppendZipToExeAsync(string zipPath, string outputPath, long bootstrapperLength, CancellationToken ct)
    {
        // Append ZIP data to the existing output file
        var zipBytes = await File.ReadAllBytesAsync(zipPath, ct);

        await using (var fs = new FileStream(outputPath, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            await fs.WriteAsync(zipBytes, ct);

            // Append footer: 8 bytes offset + 7 bytes magic ("STUPEND")
            var offsetBytes = BitConverter.GetBytes(bootstrapperLength);
            await fs.WriteAsync(offsetBytes, ct);
            await fs.WriteAsync(EndMagic, ct);
            await fs.FlushAsync(ct);
        }

        // Diagnostic: verify footer was written
        try
        {
            var diagLog = Path.Combine(Path.GetTempPath(), "ForgeTekAppendDiag.txt");
            using var verifyFs = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            verifyFs.Seek(-Math.Min(20, verifyFs.Length), SeekOrigin.End);
            var tail = new byte[Math.Min(20, verifyFs.Length)];
            verifyFs.ReadExactly(tail);
            var info = new FileInfo(outputPath);
            File.WriteAllText(diagLog,
                $"Path: {outputPath}\r\nBootstrapper length (offset): {bootstrapperLength} bytes\r\nZIP: {zipPath} ({zipBytes.Length} bytes)\r\nOutput size: {info.Length}\r\nLast 20 bytes: {BitConverter.ToString(tail)}\r\nExpected last 7: {BitConverter.ToString(EndMagic)}\r\n");
        }
        catch { }
    }

    private static string? FindLaunchExeOnDisk(AppEntry appEntry, string launchExeName)
    {
        foreach (var version in appEntry.Versions)
        {
            foreach (var file in version.Files)
            {
                if (!file.IsDebug &&
                    launchExeName.Equals(Path.GetFileName(file.Path), StringComparison.OrdinalIgnoreCase))
                {
                    var fullPath = Path.Combine(appEntry.FolderPath, file.Path);
                    if (File.Exists(fullPath))
                        return fullPath;
                }
            }
        }

        return null;
    }

    private static string? LocateBootstrapper()
    {
        var baseDir = AppContext.BaseDirectory;

        // Strategy: look in the bootstrapper's published single-file output
        // Build target places it at: SetupBootstrapper\bin\{cfg}\net10.0-windows\SetupBootstrapper\SetupBootstrapper.exe
        var solutionDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
        var buildCfg = GetConfiguration();

        var candidates = new[]
        {
            // Published single-file output (from MSBuild target)
            Path.Combine(solutionDir, "SetupBootstrapper", "bin", buildCfg,
                "net10.0-windows", "SetupBootstrapper", "SetupBootstrapper.exe"),
            // Regular build output alongside the main app
            Path.Combine(baseDir, "SetupBootstrapper.exe"),
            // Direct build output
            Path.Combine(solutionDir, "SetupBootstrapper", "bin", buildCfg,
                "net10.0-windows", "SetupBootstrapper.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? LocateBootstrapperCsproj()
    {
        var baseDir = AppContext.BaseDirectory;
        var solutionDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
        var candidate = Path.Combine(solutionDir, "SetupBootstrapper", "SetupBootstrapper.csproj");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? LocateUninstaller()
    {
        var baseDir = AppContext.BaseDirectory;
        var solutionDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
        var buildCfg = GetConfiguration();

        var candidates = new[]
        {
            // Published by the PublishSetupUninstaller target alongside the main app output.
            Path.Combine(baseDir, "SetupUninstaller", "SetupUninstaller.exe"),
            // AOT publish output under the uninstaller project.
            Path.Combine(solutionDir, "SetupUninstaller", "bin", buildCfg,
                "net10.0-windows", "win-x64", "publish", "SetupUninstaller.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string GetConfiguration()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }
}

// ── Manifest models for installer ──────────────────────────────────────────

internal sealed class InstallManifest
{
    public string SetupName { get; set; } = string.Empty;
    public string SetupVersion { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string EulaText { get; set; } = string.Empty;
    public string? BannerImageName { get; set; }
    public string? LaunchAppName { get; set; }
    public string? LaunchAppDir { get; set; }
    public string? LaunchExeName { get; set; }
    public string? BackgroundMode { get; set; }
    public string? BackgroundColor1 { get; set; }
    public string? BackgroundColor2 { get; set; }
    public string? BackgroundGradientDirection { get; set; }
    public string? BackgroundImageName { get; set; }
    public bool FixedSize { get; set; }
    public List<InstallAppManifest> Apps { get; set; } = [];
    public List<InstallRedistManifest> Redists { get; set; } = [];
}

internal sealed class InstallAppManifest
{
    public string Name { get; set; } = string.Empty;
    public string DefaultInstallDir { get; set; } = string.Empty;
    public string? LaunchExeName { get; set; }
    public string? IconFileName { get; set; }
    public bool CreateShortcut { get; set; }
    public List<string> RunAsAdminExes { get; set; } = [];
    public List<RegistryEntryManifestInternal> RegistryEntries { get; set; } = [];
}

internal sealed class RegistryEntryManifestInternal
{
    public string Root { get; set; } = "HKCU";
    public string KeyPath { get; set; } = string.Empty;
    public string ValueName { get; set; } = string.Empty;
    public string ValueData { get; set; } = string.Empty;
    public string ValueKind { get; set; } = "String";
}

internal sealed class InstallRedistManifest
{
    public string Name { get; set; } = string.Empty;
    public string ExeName { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string DetectionKeyPath { get; set; } = string.Empty;
    public string DetectionValueName { get; set; } = string.Empty;
    public string DetectionExpectedValue { get; set; } = string.Empty;
}
