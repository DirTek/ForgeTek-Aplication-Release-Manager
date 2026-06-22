using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ForgeTekApplicationReleaseManager.Helpers;
using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services;

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

    /// <summary>Fixed attribution shown in every generated installer's footer. Intentionally not
    /// operator-editable so the ForgeTek watermark can't be removed or rebranded from a bundle.</summary>
    private const string ForgeTekWatermark = "Installer by ForgeTek Application Release Manager";

    private readonly IStorageService _storage;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly ISigningService _signing;
    private readonly ISetupStorageService _setupStorage;
    private readonly ISessionService _session;

    public SetupService(IStorageService storage, ISettingsService settings, ILogService log,
        ISigningService signing, ISetupStorageService setupStorage, ISessionService session)
    {
        _storage = storage;
        _settings = settings;
        _log = log;
        _signing = signing;
        _setupStorage = setupStorage;
        _session = session;
    }

    // Generation is an ordered set of steps over a shared SetupGenContext. Each step is small and
    // independently testable; this method just drives them and guarantees temp-dir cleanup.
    public async Task<string> GenerateAsync(SetupBundle bundle, IProgress<SetupProgressInfo> progress, CancellationToken ct = default)
    {
        // Locate the pre-built bootstrapper (used as-is when no custom icon is needed, and as a
        // fallback if an on-demand branded publish fails).
        var prebuilt = LocateBootstrapper();
        if (prebuilt is null && LocateBootstrapperCsproj() is null)
            throw new FileNotFoundException(
                "SetupBootstrapper.exe not found. Build the solution first to compile the bootstrapper.");

        progress.Report(new SetupProgressInfo(5, "Bootstrapper found."));

        var ctx = new SetupGenContext(bundle, progress, ct)
        {
            PrebuiltBootstrapper = prebuilt,
            StagingDir = Path.Combine(Path.GetTempPath(), "ForgeTekSetupGen", Guid.NewGuid().ToString("N")),
            ZipDir = Path.Combine(Path.GetTempPath(), "ForgeTekSetupZip"),
            IconTempDir = Path.Combine(Path.GetTempPath(), "ForgeTekSetupIcon", Guid.NewGuid().ToString("N")),
            PublishDir = Path.Combine(Path.GetTempPath(), "ForgeTekBootstrapper", Guid.NewGuid().ToString("N")),
        };
        Directory.CreateDirectory(ctx.StagingDir);
        Directory.CreateDirectory(ctx.ZipDir);

        try
        {
            await CollectAppsAsync(ctx);     // → ctx.ManifestApps (+ staged app files)
            await CollectRedistsAsync(ctx);  // → ctx.Redists (+ staged redist exes)
            await StageActionsAsync(ctx);    // → ctx.PreActions/PostActions (+ staged action files)
            StageImages(ctx);                // → ctx.BannerName, ctx.BackgroundName
            await BuildManifestAsync(ctx);   // writes install.json
            StageUninstaller(ctx);           // stages the AOT uninstaller (if built)
            CreateZip(ctx);                  // → ctx.ZipPath
            await BuildSetupExeAsync(ctx);   // → ctx.SetupPath (bootstrapper + appended payload)
            await SignOutputAsync(ctx);      // signs ctx.SetupPath (if requested)
            BuildPortableZip(ctx);           // optional plain {Name}_Portable.zip of the app files

            RecordHistory(ctx);

            progress.Report(new SetupProgressInfo(100, $"✔ Setup generated → {ctx.SetupPath}"));
            return ctx.SetupPath;
        }
        catch
        {
            // Build failed or was cancelled. If we'd already renamed the previous build aside for
            // "Preserve old setups", restore it so the user's current setup isn't left corrupt.
            RestoreArchivedSetup(ctx);
            throw;
        }
        finally
        {
            DeleteDir(ctx.StagingDir);
            DeleteDir(ctx.ZipDir);
            DeleteDir(ctx.IconTempDir);
            DeleteDir(ctx.PublishDir);
        }

        static void DeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }

    // Mutable state threaded through the generation steps.
    private sealed class SetupGenContext(SetupBundle bundle, IProgress<SetupProgressInfo> progress, CancellationToken ct)
    {
        public SetupBundle Bundle { get; } = bundle;
        public IProgress<SetupProgressInfo> Progress { get; } = progress;
        public CancellationToken Ct { get; } = ct;

        public required string StagingDir { get; init; }
        public required string ZipDir { get; init; }
        public required string IconTempDir { get; init; }
        public required string PublishDir { get; init; }
        public string? PrebuiltBootstrapper { get; init; }

        public List<InstallAppManifest> ManifestApps { get; } = [];
        public List<InstallRedistManifest> Redists { get; } = [];
        public List<InstallActionManifest> PreActions { get; } = [];
        public List<InstallActionManifest> PostActions { get; } = [];
        public string? BannerName { get; set; }
        public string? BackgroundName { get; set; }
        public string ZipPath { get; set; } = string.Empty;
        public string SetupPath { get; set; } = string.Empty;

        /// <summary>When "Preserve old setups" renamed a prior build, its new path (for history).</summary>
        public string? ArchivedPath { get; set; }
    }

    // ── Generation steps ──────────────────────────────────────────────────────

    // Stages each selected app's files under apps/<dir> and builds its manifest entry.
    private async Task CollectAppsAsync(SetupGenContext ctx)
    {
        var (bundle, progress, ct) = (ctx.Bundle, ctx.Progress, ctx.Ct);
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
            var appStagingDir = Path.Combine(ctx.StagingDir, "apps", appDirName);
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

            ctx.ManifestApps.Add(new InstallAppManifest
            {
                Name = appEntry.Name,
                DefaultInstallDir = appDirName,
                LaunchExeName = appRef.LaunchExeName,
                IconFileName = appRef.SetupIconPath,
                CreateShortcut = appRef.CreateShortcut,
                IsRequired = !appRef.IsOptional,
                IsSelected = appRef.IsOptional ? appRef.DefaultSelected : true,
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

        if (ctx.ManifestApps.Count == 0)
            throw new InvalidOperationException("No apps selected or no apps have files.");
    }

    // Stages redistributable installers under redist/.
    private async Task CollectRedistsAsync(SetupGenContext ctx)
    {
        var (bundle, progress, ct) = (ctx.Bundle, ctx.Progress, ctx.Ct);
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

            var redistDir = Path.Combine(ctx.StagingDir, "redist");
            Directory.CreateDirectory(redistDir);

            var exeName = Path.GetFileName(redist.SourcePath);
            var targetPath = Path.Combine(redistDir, exeName);
            await using (var src = File.OpenRead(redist.SourcePath))
            await using (var dst = File.Create(targetPath))
                await src.CopyToAsync(dst, ct);

            ctx.Redists.Add(new InstallRedistManifest
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
    }

    // Builds the Pre/Post action manifests and stages any referenced local script/exe files under
    // actions/ so the bootstrapper can run them. Inline-PS, Service*, and DeleteFiles carry no file.
    private async Task StageActionsAsync(SetupGenContext ctx)
    {
        var (bundle, progress, ct) = (ctx.Bundle, ctx.Progress, ctx.Ct);
        if (bundle.CustomActions.Count == 0)
            return;

        progress.Report(new SetupProgressInfo(58, "Staging custom actions…"));
        var actionsDir = Path.Combine(ctx.StagingDir, "actions");
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var action in bundle.CustomActions)
        {
            ct.ThrowIfCancellationRequested();

            string? stagedName = null;

            // RunExecutable / RunPowerShell may reference a local file to bundle into the package.
            var stagesFile = action.Type is CustomActionType.RunExecutable or CustomActionType.RunPowerShell;
            if (stagesFile && !string.IsNullOrWhiteSpace(action.Target) && File.Exists(action.Target))
            {
                Directory.CreateDirectory(actionsDir);

                // Keep the original name where possible; de-dupe collisions.
                var baseName = StorageService.Sanitize(Path.GetFileName(action.Target));
                stagedName = baseName;
                var n = 1;
                while (!usedNames.Add(stagedName))
                    stagedName = $"{Path.GetFileNameWithoutExtension(baseName)}-{n++}{Path.GetExtension(baseName)}";

                await using (var src = File.OpenRead(action.Target))
                await using (var dst = File.Create(Path.Combine(actionsDir, stagedName)))
                    await src.CopyToAsync(dst, ct);
            }

            var manifestAction = new InstallActionManifest
            {
                Type = action.Type.ToString(),
                Label = string.IsNullOrWhiteSpace(action.Label) ? action.Type.ToString() : action.Label.Trim(),
                Target = action.Target?.Trim() ?? string.Empty,
                Arguments = action.Arguments?.Trim() ?? string.Empty,
                InlineScript = action.InlineScript ?? string.Empty,
                StagedFileName = stagedName,
                IgnoreFailure = action.IgnoreFailure,
                TimeoutSeconds = action.TimeoutSeconds,
            };

            if (action.Timing == CustomActionTiming.PreInstall)
                ctx.PreActions.Add(manifestAction);
            else
                ctx.PostActions.Add(manifestAction);
        }

        progress.Report(new SetupProgressInfo(59,
            $"✓ {bundle.CustomActions.Count} custom action(s) staged."));
    }

    // Copies the optional banner and full-window background images into the staging root.
    private void StageImages(SetupGenContext ctx)
    {
        var (bundle, progress) = (ctx.Bundle, ctx.Progress);

        if (!string.IsNullOrWhiteSpace(bundle.BannerImage) && File.Exists(bundle.BannerImage))
        {
            ctx.BannerName = "banner" + Path.GetExtension(bundle.BannerImage);
            File.Copy(bundle.BannerImage, Path.Combine(ctx.StagingDir, ctx.BannerName), overwrite: true);
            progress.Report(new SetupProgressInfo(55, "✓ Banner image added."));
        }

        if (bundle.BackgroundMode == "Image"
            && !string.IsNullOrWhiteSpace(bundle.BackgroundImage) && File.Exists(bundle.BackgroundImage))
        {
            ctx.BackgroundName = "background" + Path.GetExtension(bundle.BackgroundImage);
            File.Copy(bundle.BackgroundImage, Path.Combine(ctx.StagingDir, ctx.BackgroundName), overwrite: true);
            progress.Report(new SetupProgressInfo(56, "✓ Background image added."));
        }
    }

    // Builds install.json (apps, redists, launch target, appearance) from the staged content.
    private async Task BuildManifestAsync(SetupGenContext ctx)
    {
        var (bundle, progress, ct) = (ctx.Bundle, ctx.Progress, ctx.Ct);
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
                launchApp = ctx.ManifestApps.FirstOrDefault(m => m.DefaultInstallDir == dir);
            }
        }

        var installManifest = new InstallManifest
        {
            SetupName = bundle.Name,
            SetupVersion = string.IsNullOrWhiteSpace(bundle.Version)
                ? DateTime.Now.ToString("yyyy.MMdd.HHmm") : bundle.Version,
            CompanyName = _settings.Global.CompanyName,
            EulaText = bundle.EulaText,
            BannerImageName = ctx.BannerName,
            LaunchAppName = launchApp?.Name,
            LaunchAppDir = launchApp?.DefaultInstallDir,
            LaunchExeName = launchApp is not null ? bundle.LaunchExeName : null,
            BackgroundMode = bundle.BackgroundMode,
            BackgroundColor1 = bundle.BackgroundColor1,
            BackgroundColor2 = bundle.BackgroundColor2,
            BackgroundGradientDirection = bundle.BackgroundGradientDirection,
            BackgroundImageName = ctx.BackgroundName,
            FixedSize = bundle.FixedSize,
            FooterWatermark = ForgeTekWatermark,
            AccentColor = NullIfBlank(bundle.AccentColor),
            AccentHoverColor = NullIfBlank(bundle.AccentHoverColor),
            ButtonTextColor = NullIfBlank(bundle.ButtonTextColor),
            TextColor = NullIfBlank(bundle.TextColor),
            SurfaceColor = NullIfBlank(bundle.SurfaceColor),
            ButtonShape = string.IsNullOrWhiteSpace(bundle.ButtonShape) ? "Rounded" : bundle.ButtonShape.Trim(),
            Apps = ctx.ManifestApps,
            Redists = ctx.Redists,
            PreActions = ctx.PreActions,
            PostActions = ctx.PostActions,
            CompletionActions = bundle.CompletionActions
                .Where(a => !string.IsNullOrWhiteSpace(a.Target))
                .Select(a => new CompletionActionManifest
                {
                    Type = a.Kind.ToString(),
                    Label = string.IsNullOrWhiteSpace(a.Label) ? a.Target.Trim() : a.Label.Trim(),
                    Target = a.Target.Trim(),
                    DefaultChecked = a.DefaultChecked,
                })
                .ToList(),
        };

        var manifestJson = JsonSerializer.Serialize(installManifest, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(ctx.StagingDir, "install.json"), manifestJson, ct);
    }

    // Bundles the small per-app uninstaller (AOT) if it has been built. The bootstrapper copies it
    // into each app folder and injects that app's icon. If absent (AOT tools not installed yet),
    // the bootstrapper falls back to a single shared uninstaller.
    private void StageUninstaller(SetupGenContext ctx)
    {
        var uninstallerPath = LocateUninstaller();
        if (uninstallerPath is null)
            return;

        var uninstStageDir = Path.Combine(ctx.StagingDir, "__uninstaller__");
        Directory.CreateDirectory(uninstStageDir);
        File.Copy(uninstallerPath, Path.Combine(uninstStageDir, "SetupUninstaller.exe"), overwrite: true);
        ctx.Progress.Report(new SetupProgressInfo(68, "✓ Per-app uninstaller bundled."));
    }

    private void CreateZip(SetupGenContext ctx)
    {
        ctx.Progress.Report(new SetupProgressInfo(70, "Creating package archive…"));
        ctx.ZipPath = Path.Combine(ctx.ZipDir, "package.zip");
        ZipFile.CreateFromDirectory(ctx.StagingDir, ctx.ZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        ctx.Progress.Report(new SetupProgressInfo(85, $"ZIP created: {new FileInfo(ctx.ZipPath).Length / 1024.0:F1} KB"));
    }

    // Obtains the bootstrapper (branded on-demand publish or the pre-built fallback), copies it to
    // the output, appends the ZIP + footer, and verifies the footer.
    private async Task BuildSetupExeAsync(SetupGenContext ctx)
    {
        var (bundle, progress, ct) = (ctx.Bundle, ctx.Progress, ctx.Ct);
        progress.Report(new SetupProgressInfo(90, "Building setup executable…"));

        Directory.CreateDirectory(bundle.OutputFolder);
        ctx.SetupPath = Path.Combine(bundle.OutputFolder, $"{ResolveSetupFileName(bundle)}.exe");

        // "Preserve old setups": if a prior build is at the destination, rename it to
        // "{name}Setup-{previous generation date}.exe" before we overwrite it.
        if (bundle.PreserveOldSetups && File.Exists(ctx.SetupPath))
            ctx.ArchivedPath = ArchiveExistingSetup(ctx.SetupPath, bundle.LastGeneratedDate);

        // Resolve the setup icon and bake it into the bootstrapper at publish time. (Win32 resource
        // updates on the finished single-file EXE strip its overlay — see IconExtractor.)
        var setupIcoPath = TryResolveSetupIcon(bundle, ctx.IconTempDir);

        string? bootstrapperPath = null;
        if (setupIcoPath is not null)
        {
            progress.Report(new SetupProgressInfo(91, "Building branded bootstrapper…"));
            bootstrapperPath = await PublishBootstrapperWithIconAsync(setupIcoPath, ctx.PublishDir, ct);
        }

        bootstrapperPath ??= ctx.PrebuiltBootstrapper
            ?? throw new FileNotFoundException("SetupBootstrapper.exe could not be built or located.");

        // Copy bootstrapper (async stream to avoid any File.Copy attribute/lock issues).
        await using (var src = File.OpenRead(bootstrapperPath))
        await using (var dst = File.Create(ctx.SetupPath))
        {
            await src.CopyToAsync(dst, ct);
        }

        // Append ZIP + footer (offset = bootstrapper size before the appended payload).
        var bootstrapperLength = new FileInfo(ctx.SetupPath).Length;
        await AppendZipToExeAsync(ctx.ZipPath, ctx.SetupPath, bootstrapperLength, ct);

        VerifyFooter(ctx.SetupPath);
    }

    // Zips the staged app files into "{base}_Portable.zip" in the output folder — no bootstrapper,
    // no install.json, no redists. The base name reuses the file-name template (with a trailing
    // "Setup" stripped) so a portable build sits naturally next to the installer.
    private void BuildPortableZip(SetupGenContext ctx)
    {
        var bundle = ctx.Bundle;
        if (!bundle.GeneratePortableZip) return;

        var appsDir = Path.Combine(ctx.StagingDir, "apps");
        if (!Directory.Exists(appsDir)) return;

        var portablePath = Path.Combine(bundle.OutputFolder, $"{ResolvePortableFileName(bundle)}.zip");
        if (File.Exists(portablePath))
            try { File.Delete(portablePath); } catch { }

        ZipFile.CreateFromDirectory(appsDir, portablePath, CompressionLevel.Optimal, includeBaseDirectory: false);
        ctx.Progress.Report(new SetupProgressInfo(99,
            $"✔ Portable package → {portablePath} ({new FileInfo(portablePath).Length / 1024.0:F1} KB)"));
    }

    // Portable zip base name: resolved file-name template (or bundle name), with a trailing "Setup"
    // removed and "_Portable" appended.
    private string ResolvePortableFileName(SetupBundle bundle)
    {
        var raw = string.IsNullOrWhiteSpace(bundle.FileNameTemplate)
            ? bundle.Name
            : MacroEngine.Resolve(bundle.FileNameTemplate, BundleVars(bundle));
        if (string.IsNullOrWhiteSpace(raw)) raw = bundle.Name;

        raw = raw.Trim();
        if (raw.EndsWith("Setup", StringComparison.OrdinalIgnoreCase))
            raw = raw[..^"Setup".Length];
        raw = raw.TrimEnd(' ', '_', '-');
        if (string.IsNullOrWhiteSpace(raw)) raw = bundle.Name;

        return StorageService.Sanitize($"{raw}_Portable");
    }

    // Build-variable set available to a bundle's templated fields (file name, footer).
    private Dictionary<string, string> BundleVars(SetupBundle bundle)
        => MacroEngine.StandardVars(bundle.Name, bundle.Version, channel: null, company: _settings.Global.CompanyName);

    // Trims a value, returning null when it is blank (keeps optional theme colors out of the manifest).
    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Resolves the output EXE base name (no extension) from the bundle's template, sanitized.
    private string ResolveSetupFileName(SetupBundle bundle)
    {
        var raw = string.IsNullOrWhiteSpace(bundle.FileNameTemplate)
            ? bundle.Name + "Setup"
            : MacroEngine.Resolve(bundle.FileNameTemplate, BundleVars(bundle));
        if (string.IsNullOrWhiteSpace(raw)) raw = bundle.Name + "Setup";
        return StorageService.Sanitize(raw);
    }

    // Renames an existing Setup.exe to "{base}-{stamp}.exe" so it isn't overwritten. The stamp is the
    // file's previous generation date when known, else its last-write time. Returns the new path.
    // Appends a "Past Bundles" history entry for the setup just generated.
    private void RecordHistory(SetupGenContext ctx)
    {
        try
        {
            var exists = File.Exists(ctx.SetupPath);
            var size = exists ? new FileInfo(ctx.SetupPath).Length : 0;
            // SHA256 of the installer (winget needs it; also useful for verification).
            string? sha256 = null;
            if (exists)
            {
                try { sha256 = HashUtil.Sha256File(ctx.SetupPath); }
                catch (Exception ex) { _log.Write("SetupGen", $"Could not hash setup: {ex.Message}"); }
            }
            // Snapshot each app's version at generation time so per-app version history can show
            // when a setup that shipped that version was built.
            var appVersions = ctx.Bundle.Apps
                .Where(a => !string.IsNullOrEmpty(a.AppId))
                .GroupBy(a => a.AppId)
                .ToDictionary(g => g.Key,
                    g => _storage.GetById(g.Key)?.LatestVersion?.VersionNumber ?? string.Empty);

            _setupStorage.AddHistory(new GeneratedSetupRecord
            {
                BundleId      = ctx.Bundle.Id,
                BundleName    = ctx.Bundle.Name,
                Version       = ctx.Bundle.Version,
                GeneratedDate = DateTime.Now,
                GeneratedBy   = _session.ActorName,
                OutputPath    = ctx.SetupPath,
                FileSizeBytes = size,
                Sha256        = sha256,
                ArchivedPath  = ctx.ArchivedPath,
                AppVersions   = appVersions,
            });
        }
        catch (Exception ex)
        {
            _log.Write("SetupGen", $"Could not record setup history: {ex.Message}");
        }
    }

    // Undoes ArchiveExistingSetup when generation didn't finish: deletes the partial new file and
    // moves the preserved previous build back into place.
    private void RestoreArchivedSetup(SetupGenContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.ArchivedPath) || !File.Exists(ctx.ArchivedPath)) return;
        try
        {
            if (File.Exists(ctx.SetupPath)) File.Delete(ctx.SetupPath);
            File.Move(ctx.ArchivedPath, ctx.SetupPath);
            _log.Write("SetupGen", $"Restored previous setup after incomplete build → {ctx.SetupPath}");
            ctx.ArchivedPath = null;
        }
        catch (Exception ex)
        {
            _log.Write("SetupGen", $"Could not restore previous setup: {ex.Message}");
        }
    }

    private string? ArchiveExistingSetup(string setupPath, DateTime? previousGeneratedDate)
    {
        var dir   = Path.GetDirectoryName(setupPath)!;
        var name  = Path.GetFileNameWithoutExtension(setupPath);
        var ext   = Path.GetExtension(setupPath);
        var stamp = (previousGeneratedDate ?? File.GetLastWriteTime(setupPath)).ToString("yyyyMMdd-HHmmss");

        var archived = Path.Combine(dir, $"{name}-{stamp}{ext}");
        // Avoid clobbering an archive already taken this same second.
        var n = 1;
        while (File.Exists(archived))
            archived = Path.Combine(dir, $"{name}-{stamp}-{n++}{ext}");

        try
        {
            File.Move(setupPath, archived);
            _log.Write("SetupGen", $"Preserved previous setup → {archived}");
            return archived;
        }
        catch (Exception ex)
        {
            _log.Write("SetupGen", $"Could not preserve previous setup: {ex.Message}");
            return null;
        }
    }

    private static void VerifyFooter(string setupPath)
    {
        using var verifyFs = new FileStream(setupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
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

    // Authenticode-signs the finished setup EXE when requested and a cert is configured. Runs last
    // so the signature covers the appended payload and the cert table is the final bytes.
    private async Task SignOutputAsync(SetupGenContext ctx)
    {
        var global = _settings.Global;
        var signToolPath = ctx.Bundle.SignOutput && (global.UseStoreCert || !string.IsNullOrWhiteSpace(global.GlobalCertPath))
            ? _signing.FindSignTool() : null;

        if (signToolPath is null)
            return;

        ctx.Progress.Report(new SetupProgressInfo(96, "Signing setup executable…"));
        var silentProgress = new Progress<string>();
        await _signing.SignFilesAsync(signToolPath,
            [ctx.SetupPath],
            global.UseStoreCert ? null : global.GlobalCertPath,
            global.UseStoreCert ? null : global.GlobalCertPassword,
            global.UseStoreCert ? global.StoreCertThumbprint : null,
            silentProgress, ctx.Ct);
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
    public string? FooterWatermark { get; set; }
    // Color theme + button style (null = keep the installer's built-in dark palette).
    public string? AccentColor { get; set; }
    public string? AccentHoverColor { get; set; }
    public string? ButtonTextColor { get; set; }
    public string? TextColor { get; set; }
    public string? SurfaceColor { get; set; }
    public string ButtonShape { get; set; } = "Rounded";
    public List<InstallAppManifest> Apps { get; set; } = [];
    public List<InstallRedistManifest> Redists { get; set; } = [];
    public List<InstallActionManifest> PreActions { get; set; } = [];
    public List<InstallActionManifest> PostActions { get; set; } = [];
    public List<CompletionActionManifest> CompletionActions { get; set; } = [];
}

internal sealed class CompletionActionManifest
{
    public string Type { get; set; } = string.Empty;   // "OpenUrl" | "OpenFile"
    public string Label { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public bool DefaultChecked { get; set; } = true;
}

internal sealed class InstallActionManifest
{
    public string Type { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string InlineScript { get; set; } = string.Empty;
    /// <summary>File name staged under "actions/" (set when Target was a local file).</summary>
    public string? StagedFileName { get; set; }
    public bool IgnoreFailure { get; set; }
    public int TimeoutSeconds { get; set; }
}

internal sealed class InstallAppManifest
{
    public string Name { get; set; } = string.Empty;
    public string DefaultInstallDir { get; set; } = string.Empty;
    public string? LaunchExeName { get; set; }
    public string? IconFileName { get; set; }
    public bool CreateShortcut { get; set; }
    /// <summary>False = obligatory (shown checked + locked). True = the user may deselect it.</summary>
    public bool IsRequired { get; set; } = true;
    /// <summary>Initial checkbox state for optional apps (always true for required apps).</summary>
    public bool IsSelected { get; set; } = true;
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
