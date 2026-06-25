using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace AppUpdater;

// ── Package + plan models (mirror FARM's PackagingService header + the app's update-plan handoff) ──

internal record PackageHeader(
    string App,
    string Version,
    string Type,
    string? BaseVersion,
    List<PackageFile> Files,
    List<PackageFile> ExpectedFiles,
    List<string> RemovedFiles);

internal record PackageFile(string Path, string Hash, long Size);

internal record UpdatePlan(
    int Schema,
    string AppKey,
    string CurrentVersion,
    string TargetVersion,
    List<PlanPackage> Packages,
    PlanRef? LatestFull,
    PlanRef? Latest);

internal record PlanPackage(string Version, string File, string Type, string Base);

internal record PlanRef(string Version, string Url);

/// <summary>Progress sink the WPF window implements; keeps <see cref="UpdaterCore"/> UI-agnostic.</summary>
internal interface IUpdaterUi
{
    /// <summary>Sets the single-line status/heading.</summary>
    void Status(string text);
    /// <summary>Appends a line to the detail log.</summary>
    void Log(string line);
    /// <summary>Sets the progress fraction 0..1, or a negative value for indeterminate.</summary>
    void Progress(double fraction);
}

internal sealed record UpdateResult(bool Success, string? AppExePath, string? Error);

/// <summary>
/// Applies a staged FARM update: verifies the FTUP package(s), extracts them over the install,
/// honours RemovedFiles, self-heals against ExpectedFiles, then reports the app exe to relaunch.
/// Logic is ported from the reference STLVerse updater, parameterized via <see cref="UpdaterConfig"/>
/// and reporting through <see cref="IUpdaterUi"/> instead of the console.
/// </summary>
internal sealed class UpdaterCore(UpdaterConfig config, IUpdaterUi ui)
{
    private const int ChecksumLength = 32;
    private static readonly byte[] MagicBytes = "FTUP"u8.ToArray();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public async Task<UpdateResult> RunAsync()
    {
        try
        {
            var installDir = AppContext.BaseDirectory;
            var appExe = Path.Combine(installDir, config.AppExe);
            var updatesDir = Path.Combine(installDir, config.UpdatesFolder);

            ui.Status("Checking installation…");
            if (string.IsNullOrWhiteSpace(config.AppExe))
                return new UpdateResult(false, null, "No application executable is configured (updater.json).");
            if (!File.Exists(appExe))
                return new UpdateResult(false, null, $"{config.AppExe} not found in {installDir}");
            ui.Log($"Found: {appExe}");

            ui.Status($"Closing {Path.GetFileNameWithoutExtension(config.AppExe)}…");
            await CloseApplicationAsync(config.AppExe);

            ui.Status("Applying update…");
            ui.Progress(-1);
            var planPath = Path.Combine(updatesDir, config.PlanFile);
            var applied = File.Exists(planPath)
                ? await ApplyFromPlanAsync(planPath, updatesDir, installDir)
                : await ApplyLegacySingleAsync(updatesDir, installDir);

            if (!applied.Success)
                return applied;

            ui.Progress(1);
            ui.Status("Update complete.");
            return new UpdateResult(true, appExe, null);
        }
        catch (Exception ex)
        {
            return new UpdateResult(false, null, ex.Message);
        }
    }

    // ── Plan-driven apply (cumulative incrementals + ExpectedFiles self-heal) ──────────

    private async Task<UpdateResult> ApplyFromPlanAsync(string planPath, string updatesDir, string installDir)
    {
        var plan = ParsePlan(planPath);
        if (plan is null)
            return new UpdateResult(false, null, $"Could not parse {config.PlanFile}.");

        if (!string.IsNullOrEmpty(plan.AppKey) && !string.IsNullOrEmpty(config.AppKey) &&
            !plan.AppKey.Equals(config.AppKey, StringComparison.OrdinalIgnoreCase))
            return new UpdateResult(false, null, $"Plan is for a different app: \"{plan.AppKey}\".");

        ui.Log($"Plan: {plan.CurrentVersion} -> {plan.TargetVersion} ({plan.Packages.Count} package(s))");

        if (plan.Packages.Count == 0)
            return new UpdateResult(false, null, "Plan lists no packages to apply.");

        PackageHeader? lastHeader = null;
        var idx = 0;
        foreach (var pkg in plan.Packages)
        {
            idx++;
            var path = Path.Combine(updatesDir, pkg.File);
            ui.Status($"Applying {idx}/{plan.Packages.Count}: {pkg.File}");
            ui.Log($"[{idx}/{plan.Packages.Count}] {pkg.File} ({pkg.Type} {pkg.Version})");
            if (!File.Exists(path))
                return new UpdateResult(false, null, $"Staged package missing: {pkg.File}");

            var (header, error) = await ApplyPackageAsync(path, installDir);
            if (header is null)
                return new UpdateResult(false, null, error);
            lastHeader = header;
        }

        // Self-heal against the full expected file set of the last (newest) package.
        if (lastHeader is { ExpectedFiles.Count: > 0 })
        {
            ui.Status("Verifying files…");
            var missing = FindMissingExpected(lastHeader, installDir);
            if (missing.Count == 0)
            {
                ui.Log("All expected files present.");
            }
            else
            {
                ui.Log($"{missing.Count} file(s) missing/mismatched — repairing from baseline…");
                if (!await SelfHealAsync(plan, updatesDir, installDir))
                    return new UpdateResult(false, null, "Self-heal could not complete the install. The app may be incomplete.");
            }
        }
        else
        {
            ui.Log("No ExpectedFiles in package — skipping self-heal (older packager).");
        }

        CleanupStaged(plan, updatesDir, planPath);
        return new UpdateResult(true, null, null);
    }

    private async Task<bool> SelfHealAsync(UpdatePlan plan, string updatesDir, string installDir)
    {
        if (plan.LatestFull is null || string.IsNullOrEmpty(plan.LatestFull.Url))
        {
            ui.Log("No latestFull baseline available to repair from.");
            return false;
        }

        var ext = config.PackageExtension;
        var fullPath = Path.Combine(updatesDir, $"_heal_full_{Sanitize(plan.LatestFull.Version)}.{ext}");
        ui.Status($"Downloading baseline {plan.LatestFull.Version}…");
        await DownloadAsync(plan.LatestFull.Url, fullPath);
        var (top, _) = await ApplyPackageAsync(fullPath, installDir);
        try { File.Delete(fullPath); } catch { }
        if (top is null) return false;

        // Re-apply the latest cumulative incremental on top, if it is newer than the baseline.
        if (plan.Latest is not null && !string.IsNullOrEmpty(plan.Latest.Url) &&
            !string.Equals(plan.Latest.Version, plan.LatestFull.Version, StringComparison.OrdinalIgnoreCase))
        {
            var latestPath = Path.Combine(updatesDir, $"_heal_latest_{Sanitize(plan.Latest.Version)}.{ext}");
            ui.Status($"Downloading latest {plan.Latest.Version}…");
            await DownloadAsync(plan.Latest.Url, latestPath);
            var (latestHeader, _) = await ApplyPackageAsync(latestPath, installDir);
            try { File.Delete(latestPath); } catch { }
            if (latestHeader is null) return false;
            top = latestHeader;
        }

        var stillMissing = FindMissingExpected(top, installDir);
        if (stillMissing.Count == 0)
        {
            ui.Log("Repair complete — install verified.");
            return true;
        }

        ui.Log($"Still {stillMissing.Count} file(s) missing after repair.");
        return false;
    }

    private void CleanupStaged(UpdatePlan plan, string updatesDir, string planPath)
    {
        foreach (var pkg in plan.Packages)
        {
            try { File.Delete(Path.Combine(updatesDir, pkg.File)); } catch { }
        }
        try { File.Delete(planPath); } catch { }
    }

    // ── Legacy single-package apply (no handoff present) ───────────────────────────────

    private async Task<UpdateResult> ApplyLegacySingleAsync(string updatesDir, string installDir)
    {
        ui.Log($"No {config.PlanFile} found — using single-package mode.");
        var packagePath = FindLatestPackage(updatesDir);
        if (string.IsNullOrEmpty(packagePath))
            return new UpdateResult(false, null, $"No .{config.PackageExtension} package found in {config.UpdatesFolder}\\.");

        ui.Log($"Found: {Path.GetFileName(packagePath)}");
        var (header, error) = await ApplyPackageAsync(packagePath, installDir);
        if (header is null)
            return new UpdateResult(false, null, error);

        try { File.Delete(packagePath); } catch { }
        return new UpdateResult(true, null, null);
    }

    // ── Shared apply primitive: verify -> parse -> extract -> remove ───────────────────

    private async Task<(PackageHeader? Header, string? Error)> ApplyPackageAsync(string packagePath, string installDir)
    {
        if (!await VerifyPackageAsync(packagePath))
            return (null, $"Package verification failed: {Path.GetFileName(packagePath)}");

        var header = await ParseHeaderAsync(packagePath);
        if (header is null ||
            (!string.IsNullOrEmpty(config.AppKey) && !header.App.Equals(config.AppKey, StringComparison.OrdinalIgnoreCase)))
            return (null, $"Invalid package (wrong app key): {Path.GetFileName(packagePath)}");

        await ExtractPackageAsync(packagePath, installDir);
        RemoveDeprecated(header, installDir);
        return (header, null);
    }

    private void RemoveDeprecated(PackageHeader header, string installDir)
    {
        if (header.RemovedFiles is null || header.RemovedFiles.Count == 0)
            return;

        foreach (var file in header.RemovedFiles)
        {
            var fullPath = Path.Combine(installDir, file.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
            {
                try
                {
                    File.Delete(fullPath);
                    ui.Log($"Deleted: {file}");
                }
                catch (Exception ex)
                {
                    ui.Log($"Warning: could not delete {file}: {ex.Message}");
                }
            }
        }
    }

    // ── Self-heal verification ─────────────────────────────────────────────────────────

    private static List<PackageFile> FindMissingExpected(PackageHeader header, string installDir)
    {
        var missing = new List<PackageFile>();
        foreach (var ef in header.ExpectedFiles)
        {
            var fullPath = Path.Combine(installDir, ef.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                missing.Add(ef);
                continue;
            }

            var expected = NormalizeHash(ef.Hash);
            if (string.IsNullOrEmpty(expected))
                continue; // nothing to compare against — presence is enough

            if (!string.Equals(Sha256Hex(fullPath), expected, StringComparison.OrdinalIgnoreCase))
                missing.Add(ef);
        }
        return missing;
    }

    private static string NormalizeHash(string hash) =>
        string.IsNullOrEmpty(hash)
            ? ""
            : hash.StartsWith("sha256-", StringComparison.OrdinalIgnoreCase)
                ? hash["sha256-".Length..]
                : hash;

    private static string Sha256Hex(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value;
    }

    // ── Download (self-heal only) ──────────────────────────────────────────────────────

    private async Task DownloadAsync(string url, string destPath)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);

        var buf = new byte[65536];
        long readTotal = 0;
        int read;
        while ((read = await src.ReadAsync(buf)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read));
            readTotal += read;
            if (total > 0) ui.Progress((double)readTotal / total);
        }
    }

    // ── Plan parsing ───────────────────────────────────────────────────────────────────

    private UpdatePlan? ParsePlan(string planPath)
    {
        try
        {
            // ReadAllText strips any UTF-8 BOM the app's writer may have added (JsonDocument.Parse
            // on raw bytes would otherwise choke on a leading BOM).
            using var doc = JsonDocument.Parse(File.ReadAllText(planPath));
            var root = doc.RootElement;

            var schema = GetInt(root, 0, "schema");
            var appKey = GetStr(root, "appKey", "appkey", "app");
            var current = GetStr(root, "currentVersion", "current");
            var target = GetStr(root, "targetVersion", "target");

            var packages = new List<PlanPackage>();
            if (root.TryGetProperty("packages", out var pkgsEl) && pkgsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pkgsEl.EnumerateArray())
                {
                    packages.Add(new PlanPackage(
                        GetStr(p, "version"),
                        GetStr(p, "file"),
                        GetStr(p, "type"),
                        GetStr(p, "base")));
                }
            }

            return new UpdatePlan(schema, appKey, current, target, packages,
                ParsePlanRef(root, "latestFull"),
                ParsePlanRef(root, "latest"));
        }
        catch
        {
            return null;
        }
    }

    private static PlanRef? ParsePlanRef(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object)
            return null;
        var version = GetStr(el, "version");
        var url = GetStr(el, "url");
        return string.IsNullOrEmpty(url) ? null : new PlanRef(version, url);
    }

    private static string GetStr(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        return "";
    }

    private static int GetInt(JsonElement el, int fallback, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
                return i;
        return fallback;
    }

    // ── Package primitives ─────────────────────────────────────────────────────────────

    private string? FindLatestPackage(string updatesDir)
    {
        if (!Directory.Exists(updatesDir))
            return null;

        var packages = Directory.GetFiles(updatesDir, $"*.{config.PackageExtension}");
        if (packages.Length == 0)
            return null;

        Array.Sort(packages);
        return packages[^1];
    }

    private async Task CloseApplicationAsync(string exePath)
    {
        var processName = Path.GetFileNameWithoutExtension(exePath);
        var processes = Process.GetProcessesByName(processName);

        if (processes.Length == 0)
        {
            ui.Log("App not running.");
            return;
        }

        ui.Log($"Found {processes.Length} running instance(s)… closing…");

        foreach (var proc in processes)
        {
            try
            {
                proc.CloseMainWindow();
                if (!proc.WaitForExit(5000))
                {
                    ui.Log($"Force killing {proc.ProcessName}…");
                    proc.Kill();
                }
                proc.Dispose();
            }
            catch (Exception ex)
            {
                ui.Log($"Warning: {ex.Message}");
            }
        }

        await Task.Delay(1000);
    }

    private static async Task<bool> VerifyPackageAsync(string path)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length < ChecksumLength)
            return false;

        // Magic bytes
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
        {
            var magic = new byte[4];
            await fs.ReadExactlyAsync(magic, 0, 4);
            for (var i = 0; i < 4; i++)
                if (magic[i] != MagicBytes[i])
                    return false;
        }

        // SHA-256 over everything except the last 32 bytes
        var payloadLength = fileInfo.Length - ChecksumLength;
        using var sha = SHA256.Create();
        using var fs2 = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);

        var buf = new byte[65536];
        var remaining = payloadLength;

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(remaining, buf.Length);
            var read = await fs2.ReadAsync(buf.AsMemory(0, toRead));
            if (read == 0) return false;
            sha.TransformBlock(buf, 0, read, null, 0);
            remaining -= read;
        }

        sha.TransformFinalBlock([], 0, 0);
        var computed = sha.Hash!;

        var stored = new byte[ChecksumLength];
        if (await fs2.ReadAsync(stored.AsMemory(0, ChecksumLength)) < ChecksumLength)
            return false;

        return CryptographicOperations.FixedTimeEquals(computed, stored);
    }

    private static async Task<PackageHeader?> ParseHeaderAsync(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);

        fs.Seek(4, SeekOrigin.Begin);
        var lenBuf = new byte[4];
        await fs.ReadExactlyAsync(lenBuf, 0, 4);
        var headerLen = BitConverter.ToUInt32(lenBuf, 0);

        var headerBuf = new byte[headerLen];
        await fs.ReadExactlyAsync(headerBuf, 0, (int)headerLen);

        try
        {
            using var doc = JsonDocument.Parse(headerBuf);
            var root = doc.RootElement;

            var app = GetStr(root, "appKey", "app", "appkey");
            var version = GetStr(root, "version");
            var type = GetStr(root, "type", "packageType");
            var baseVersion = GetStr(root, "baseVersion", "base");

            var files = ReadFileList(root, "files");
            var expectedFiles = ReadFileList(root, "expectedFiles", "ExpectedFiles");

            var removedFiles = new List<string>();
            if (root.TryGetProperty("removedFiles", out var remEl) && remEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in remEl.EnumerateArray())
                {
                    var rPath = r.GetString();
                    if (!string.IsNullOrWhiteSpace(rPath))
                        removedFiles.Add(rPath);
                }
            }

            return new PackageHeader(app, version, type,
                string.IsNullOrEmpty(baseVersion) ? null : baseVersion,
                files, expectedFiles, removedFiles);
        }
        catch
        {
            return null;
        }
    }

    private static List<PackageFile> ReadFileList(JsonElement root, params string[] names)
    {
        var list = new List<PackageFile>();
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var arrEl) || arrEl.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var f in arrEl.EnumerateArray())
            {
                var filePath = GetStr(f, "path");
                var hash = GetStr(f, "hash", "checksum");
                var size = f.TryGetProperty("size", out var sEl) && sEl.ValueKind == JsonValueKind.Number
                    ? sEl.GetInt64()
                    : 0L;
                list.Add(new PackageFile(filePath, hash, size));
            }
            break; // first matching property name wins
        }
        return list;
    }

    private async Task ExtractPackageAsync(string packagePath, string targetDir)
    {
        using var fs = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);

        fs.Seek(4, SeekOrigin.Begin);
        var lenBuf = new byte[4];
        await fs.ReadExactlyAsync(lenBuf, 0, 4);
        var headerLen = BitConverter.ToUInt32(lenBuf, 0);
        fs.Seek(headerLen, SeekOrigin.Current);

        var zipStart = fs.Position;
        var zipLength = new FileInfo(packagePath).Length - zipStart - ChecksumLength;
        if (zipLength <= 0)
            throw new InvalidDataException("Package contains no ZIP payload.");

        Directory.CreateDirectory(targetDir);

        var zipData = new byte[zipLength];
        long totalRead = 0;
        var buffer = new byte[65536];
        while (totalRead < zipLength)
        {
            var toRead = (int)Math.Min(buffer.Length, zipLength - totalRead);
            var read = await fs.ReadAsync(buffer.AsMemory(0, toRead));
            if (read == 0) break;
            Array.Copy(buffer, 0, zipData, totalRead, read);
            totalRead += read;
            ui.Progress((double)totalRead / zipLength);
        }

        using var zipStream = new MemoryStream(zipData);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);

        // A process can't overwrite its own running .exe. The updater is never replaced via its plain
        // name — full packages ship "{name}_new.exe", which the app promotes on next launch. So skip an
        // entry matching our own name, but let "_new" (a different file name) extract normally.
        var selfExeName = Path.GetFileName(Environment.ProcessPath ?? "");
        if (string.IsNullOrEmpty(selfExeName))
            selfExeName = config.UpdaterExe;

        var targetFull = Path.GetFullPath(targetDir);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                continue;

            var destPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));

            // Path traversal guard
            if (!destPath.StartsWith(targetFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Path traversal detected: {entry.FullName}");

            if (!string.IsNullOrEmpty(selfExeName) &&
                string.Equals(Path.GetFileName(destPath), selfExeName, StringComparison.OrdinalIgnoreCase))
            {
                ui.Log($"Skipping self ({selfExeName}) — promoted from _new on next launch.");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var entryStream = entry.Open();
            using var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write);
            await entryStream.CopyToAsync(destStream);
        }
    }
}
