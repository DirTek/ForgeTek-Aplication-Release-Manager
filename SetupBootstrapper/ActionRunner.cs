using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SetupBootstrapper;

/// <summary>
/// Executes a bundle's custom install actions (service control, scripts, executables, file
/// cleanup). The bootstrapper runs elevated, so service and Program Files operations succeed.
/// </summary>
internal static class ActionRunner
{
    /// <summary>Runs one action. Throws on failure unless the action sets IgnoreFailure.</summary>
    public static async Task RunAsync(InstallAction a, string rootDir, string tempDir, IProgress<string> log)
    {
        try
        {
            switch (ParseType(a.Type))
            {
                case ActionKind.ServiceStop:
                    await ServiceControlAsync("stop", a, log);
                    break;
                case ActionKind.ServiceStart:
                    await ServiceControlAsync("start", a, log);
                    break;
                case ActionKind.RunPowerShell:
                    await RunPowerShellAsync(a, rootDir, tempDir, log);
                    break;
                case ActionKind.RunExecutable:
                    await RunExecutableAsync(a, rootDir, tempDir, log);
                    break;
                case ActionKind.DeleteFiles:
                    DeleteFiles(a, rootDir, log);
                    break;
                default:
                    log.Report($"  ⚠ Unknown action type '{a.Type}' — skipped.");
                    break;
            }
        }
        catch (Exception ex)
        {
            if (a.IgnoreFailure)
            {
                log.Report($"  ⚠ {Describe(a)} failed (ignored): {ex.Message}");
                return;
            }
            throw new InvalidOperationException($"{Describe(a)} failed: {ex.Message}", ex);
        }
    }

    private enum ActionKind { ServiceStop, ServiceStart, RunPowerShell, RunExecutable, DeleteFiles, Unknown }

    private static ActionKind ParseType(string type) =>
        Enum.TryParse<ActionKind>(type, ignoreCase: true, out var k) ? k : ActionKind.Unknown;

    private static string Describe(InstallAction a) =>
        string.IsNullOrWhiteSpace(a.Label) ? a.Type : a.Label;

    // ── Service control ─────────────────────────────────────────────────────
    private static async Task ServiceControlAsync(string verb, InstallAction a, IProgress<string> log)
    {
        if (string.IsNullOrWhiteSpace(a.Target))
            throw new InvalidOperationException("No service name specified.");

        log.Report($"  sc {verb} {a.Target}");
        await RunProcessAsync("sc.exe", $"{verb} \"{a.Target}\"", null, a.TimeoutSeconds, log,
            // sc returns 1062 (not started) on stop / 1056 (already running) on start — treat as success.
            okExitCodes: verb == "stop" ? [0, 1062] : [0, 1056]);

        // Poll until the service reaches the desired state (best-effort, bounded by timeout).
        var target = verb == "stop" ? "STOPPED" : "RUNNING";
        var deadline = DateTime.UtcNow.AddSeconds(a.TimeoutSeconds > 0 ? a.TimeoutSeconds : 30);
        while (DateTime.UtcNow < deadline)
        {
            var (code, output) = await CaptureProcessAsync("sc.exe", $"query \"{a.Target}\"");
            if (code != 0 || output.Contains(target, StringComparison.OrdinalIgnoreCase))
                break;
            await Task.Delay(500);
        }
    }

    // ── PowerShell ──────────────────────────────────────────────────────────
    private static async Task RunPowerShellAsync(InstallAction a, string rootDir, string tempDir, IProgress<string> log)
    {
        string scriptPath;
        bool deleteAfter = false;

        var staged = ResolveStaged(a, tempDir);
        if (staged is not null)
        {
            scriptPath = staged;
        }
        else if (!string.IsNullOrWhiteSpace(a.InlineScript))
        {
            scriptPath = Path.Combine(tempDir, $"action-{Guid.NewGuid():N}.ps1");
            await File.WriteAllTextAsync(scriptPath, ResolveTokens(a.InlineScript, rootDir));
            deleteAfter = true;
        }
        else
        {
            throw new InvalidOperationException("No script file or inline script provided.");
        }

        log.Report($"  powershell -File {Path.GetFileName(scriptPath)}");
        try
        {
            await RunProcessAsync("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {ResolveTokens(a.Arguments, rootDir)}".Trim(),
                rootDir, a.TimeoutSeconds, log);
        }
        finally
        {
            if (deleteAfter)
                try { File.Delete(scriptPath); } catch { }
        }
    }

    // ── Executable ──────────────────────────────────────────────────────────
    private static async Task RunExecutableAsync(InstallAction a, string rootDir, string tempDir, IProgress<string> log)
    {
        var exe = ResolveStaged(a, tempDir) ?? a.Target;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            throw new InvalidOperationException($"Executable not found: {a.Target}");

        log.Report($"  run {Path.GetFileName(exe)} {a.Arguments}".TrimEnd());
        await RunProcessAsync(exe, ResolveTokens(a.Arguments, rootDir), rootDir, a.TimeoutSeconds, log);
    }

    // ── Delete files ────────────────────────────────────────────────────────
    private static void DeleteFiles(InstallAction a, string rootDir, IProgress<string> log)
    {
        if (string.IsNullOrWhiteSpace(a.Target))
            throw new InvalidOperationException("No path specified.");

        foreach (var raw in a.Target.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var path = ResolveTokens(raw.Trim(), rootDir);
            if (string.IsNullOrWhiteSpace(path)) continue;

            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    log.Report($"  deleted folder {path}");
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                    log.Report($"  deleted {path}");
                }
                else
                {
                    // Support a simple wildcard in the final segment.
                    var dir = Path.GetDirectoryName(path);
                    var pattern = Path.GetFileName(path);
                    if (dir is not null && Directory.Exists(dir) && pattern.Contains('*'))
                    {
                        foreach (var f in Directory.GetFiles(dir, pattern))
                        {
                            try { File.Delete(f); log.Report($"  deleted {f}"); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not delete '{path}': {ex.Message}", ex);
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private static string? ResolveStaged(InstallAction a, string tempDir)
    {
        if (string.IsNullOrWhiteSpace(a.StagedFileName)) return null;
        var p = Path.Combine(tempDir, "actions", a.StagedFileName);
        return File.Exists(p) ? p : null;
    }

    private static string ResolveTokens(string? value, string rootDir) =>
        (value ?? string.Empty).Replace("[InstallDir]", rootDir, StringComparison.OrdinalIgnoreCase);

    private static async Task RunProcessAsync(string fileName, string arguments, string? workingDir,
        int timeoutSeconds, IProgress<string> log, int[]? okExitCodes = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDir ?? Path.GetTempPath(),
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");

        proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.Report($"    {e.Data}"); };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.Report($"    {e.Data}"); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = timeoutSeconds > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
            : new CancellationTokenSource();
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException($"Timed out after {timeoutSeconds}s.");
        }

        var ok = okExitCodes ?? [0];
        if (!ok.Contains(proc.ExitCode))
            throw new InvalidOperationException($"Exited with code {proc.ExitCode}.");
    }

    private static async Task<(int Code, string Output)> CaptureProcessAsync(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null) return (-1, string.Empty);
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, output);
    }
}
