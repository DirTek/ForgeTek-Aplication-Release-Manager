using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services;

/// <summary>
/// Generates a per-app standalone updater EXE from the <c>AppUpdater</c> template. Mirrors
/// <see cref="SetupService"/>'s bootstrapper flow: locate the template csproj, <c>dotnet publish</c>
/// a single-file self-contained EXE with the app icon baked in, and fall back to the prebuilt
/// generic updater when the SDK isn't present. Either way an <c>updater.json</c> sidecar carries the
/// functional config (app key, exe name, package extension, accent) so the same binary works branded
/// or generic.
/// </summary>
public class UpdaterService(ISettingsService settings, ILogService log, ISigningService signing) : IUpdaterService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<GeneratedUpdaterRecord> GenerateAsync(
        AppEntry entry,
        AppSettings appSettings,
        UpdaterGenOptions options,
        IProgress<string> progress,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.OutputFolder);

        var updaterName = $"{Sanitize(entry.Name)}.Updater.exe";
        var outputExe = Path.Combine(options.OutputFolder, updaterName);

        var iconTempDir = Path.Combine(Path.GetTempPath(), "ForgeTekUpdaterIcon", Guid.NewGuid().ToString("N"));
        var publishDir = Path.Combine(Path.GetTempPath(), "ForgeTekUpdaterPublish", Guid.NewGuid().ToString("N"));
        var branded = false;

        try
        {
            // 1. Resolve a brandable .ico (best-effort).
            progress.Report("Resolving application icon…");
            var icoPath = TryResolveIcon(options.IconSourcePath, iconTempDir);

            // 2. Compile-on-demand a branded single-file updater; fall back to the prebuilt one.
            progress.Report("Building updater…");
            var built = await PublishUpdaterAsync(icoPath, updaterName, publishDir, ct);

            if (built is not null)
            {
                File.Copy(built, outputExe, overwrite: true);
                branded = icoPath is not null;
                progress.Report("Built a branded updater.");
            }
            else
            {
                progress.Report("SDK build unavailable — using the prebuilt updater.");
                var prebuilt = LocatePrebuiltUpdater();
                if (prebuilt is null)
                    throw new InvalidOperationException(
                        "Could not build the updater and no prebuilt AppUpdater.exe was found.");
                File.Copy(prebuilt, outputExe, overwrite: true);
            }

            // 3. Always write the updater.json sidecar (drives both branded and prebuilt binaries).
            progress.Report("Writing updater.json…");
            var sidecarPath = Path.Combine(options.OutputFolder, "updater.json");
            WriteSidecar(entry, appSettings, options, updaterName, sidecarPath);

            // 4. Optional Authenticode signature.
            if (options.Sign)
                await TrySignAsync(outputExe, progress, ct);

            var fi = new FileInfo(outputExe);
            var record = new GeneratedUpdaterRecord
            {
                AppName = entry.Name,
                OutputPath = outputExe,
                SidecarPath = sidecarPath,
                FileSizeBytes = fi.Exists ? fi.Length : 0,
                Sha256 = TryHash(outputExe),
                Branded = branded,
            };

            progress.Report($"✔  Updater saved → {outputExe}");
            return record;
        }
        finally
        {
            TryDeleteDir(iconTempDir);
            TryDeleteDir(publishDir);
        }
    }

    // ── Build ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Publishes a fresh single-file, self-contained updater named <paramref name="updaterName"/> with
    /// <paramref name="icoPath"/> baked in (when non-null). Returns the published EXE path, or null on
    /// failure (caller falls back to the prebuilt updater).
    /// </summary>
    private async Task<string?> PublishUpdaterAsync(string? icoPath, string updaterName, string publishDir, CancellationToken ct)
    {
        var csproj = LocateUpdaterCsproj();
        if (csproj is null)
        {
            log.Write("Updater", "AppUpdater .csproj not found; cannot compile a branded updater.");
            return null;
        }

        var assemblyName = Path.GetFileNameWithoutExtension(updaterName);

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
        // Mirror the prebuilt updater (see PublishAppUpdater target). Without this a self-contained
        // single-file WPF app fails to load its native libraries.
        psi.ArgumentList.Add("-p:IncludeNativeLibrariesForSelfExtract=true");
        psi.ArgumentList.Add($"-p:AssemblyName={assemblyName}");
        if (icoPath is not null)
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
                log.Write("Updater",
                    $"Branded updater publish failed (exit {proc.ExitCode}); using prebuilt updater.\n{output}");
                return null;
            }
        }
        catch (Exception ex)
        {
            log.Write("Updater", $"Branded updater publish errored: {ex.Message}");
            return null;
        }

        var exe = Path.Combine(publishDir, updaterName);
        return File.Exists(exe) ? exe : null;
    }

    // ── Sidecar ──────────────────────────────────────────────────────────────────

    private void WriteSidecar(AppEntry entry, AppSettings appSettings, UpdaterGenOptions options,
        string updaterName, string sidecarPath)
    {
        var sidecar = new
        {
            appKey = entry.Name,
            appExe = options.AppExeName,
            updaterExe = updaterName,
            packageExtension = string.IsNullOrWhiteSpace(appSettings.PackageExtension)
                ? "ftu" : appSettings.PackageExtension!.TrimStart('.'),
            updatesFolder = "Updates",
            planFile = "update-plan.json",
            accentColor = string.IsNullOrWhiteSpace(entry.AccentColor) ? "#0A84FF" : entry.AccentColor,
            windowTitle = $"{entry.Name} Updater",
        };
        File.WriteAllText(sidecarPath, JsonSerializer.Serialize(sidecar, JsonOptions));
    }

    // ── Icon ───────────────────────────────────────────────────────────────────

    private string? TryResolveIcon(string? srcPath, string iconTempDir)
    {
        if (string.IsNullOrWhiteSpace(srcPath) || !File.Exists(srcPath))
            return null;

        // A real .ico is used directly (preserves all resolutions); anything else is extracted.
        if (".ico".Equals(Path.GetExtension(srcPath), StringComparison.OrdinalIgnoreCase))
            return srcPath;

        try
        {
            Directory.CreateDirectory(iconTempDir);
            var ico = Path.Combine(iconTempDir, "updater.ico");
            if (IconExtractor.TryExtractToIco(srcPath, ico))
                return ico;
        }
        catch (Exception ex)
        {
            log.Write("Updater", $"Icon extraction failed for '{srcPath}': {ex.Message}");
        }
        return null;
    }

    // ── Signing ──────────────────────────────────────────────────────────────────

    private async Task TrySignAsync(string exePath, IProgress<string> progress, CancellationToken ct)
    {
        var global = settings.Global;
        var signToolPath = global.UseStoreCert || !string.IsNullOrWhiteSpace(global.GlobalCertPath)
            ? signing.FindSignTool() : null;
        if (signToolPath is null)
            return;

        progress.Report("Signing updater…");
        var silent = new Progress<string>();
        await signing.SignFilesAsync(signToolPath,
            [exePath],
            global.UseStoreCert ? null : global.GlobalCertPath,
            global.UseStoreCert ? null : global.GlobalCertPassword,
            global.UseStoreCert ? global.StoreCertThumbprint : null,
            silent, ct);
    }

    // ── Locating the template / prebuilt EXE (mirrors SetupService) ────────────────

    private static string? LocateUpdaterCsproj()
    {
        var baseDir = AppContext.BaseDirectory;
        var solutionDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
        var candidate = Path.Combine(solutionDir, "AppUpdater", "AppUpdater.csproj");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? LocatePrebuiltUpdater()
    {
        var baseDir = AppContext.BaseDirectory;
        var solutionDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
        var buildCfg = GetConfiguration();

        var candidates = new[]
        {
            // Published single-file output (from the PublishAppUpdater MSBuild target).
            Path.Combine(baseDir, "AppUpdater", "AppUpdater.exe"),
            Path.Combine(solutionDir, "AppUpdater", "bin", buildCfg, "net10.0-windows", "AppUpdater", "AppUpdater.exe"),
            Path.Combine(solutionDir, "AppUpdater", "bin", buildCfg, "net10.0-windows", "win-x64", "AppUpdater.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value.Trim();
    }

    private static string? TryHash(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        }
        catch { return null; }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
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
