using System.Diagnostics;

namespace ForgeTekApplicationReleaseManager.Services;

/// <summary>Result of running an external CLI process: exit code plus the combined stdout/stderr lines.</summary>
public sealed record ProcessResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Thrown when an external tool isn't installed / not on PATH. Lets callers degrade gracefully
/// (e.g. "winget not found") instead of surfacing a raw Win32Exception.</summary>
public sealed class ToolNotFoundException(string tool, Exception inner)
    : Exception($"'{tool}' was not found. Make sure it is installed and on your PATH.", inner)
{
    public string Tool { get; } = tool;
}

/// <summary>Shared helper for running external CLI processes (git, winget, wingetcreate, dotnet, …),
/// streaming output and capturing the exit code. Extracted from GitHubService's build runner.</summary>
public static class ProcessRunner
{
    /// <summary>Runs <paramref name="fileName"/> and streams stdout/stderr to <paramref name="progress"/>.
    /// Throws <see cref="ToolNotFoundException"/> when the executable is missing,
    /// <see cref="OperationCanceledException"/> on cancel. Returns the exit code + collected output.</summary>
    public static async Task<ProcessResult> RunAsync(string fileName, string arguments,
        string? workingDir = null, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = fileName,
            Arguments              = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        if (!string.IsNullOrWhiteSpace(workingDir))
            psi.WorkingDirectory = workingDir;

        Process proc;
        try
        {
            proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // ERROR_FILE_NOT_FOUND (2) — the executable isn't on PATH.
            throw new ToolNotFoundException(fileName, ex);
        }

        using (proc)
        {
            var lines = new List<string>();
            void Capture(string? data)
            {
                if (data is null) return;
                lock (lines) lines.Add(data);
                progress?.Report(data);
            }

            proc.OutputDataReceived += (_, e) => Capture(e.Data);
            proc.ErrorDataReceived  += (_, e) => Capture(e.Data);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            string output;
            lock (lines) output = string.Join(Environment.NewLine, lines);
            return new ProcessResult(proc.ExitCode, output);
        }
    }

    /// <summary>Runs a process and throws <see cref="InvalidOperationException"/> on a non-zero exit code.</summary>
    public static async Task RunOrThrowAsync(string fileName, string arguments,
        string? workingDir = null, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var result = await RunAsync(fileName, arguments, workingDir, progress, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException($"{fileName} exited with code {result.ExitCode}.");
    }

    /// <summary>True if the named executable can be launched (used to gate optional tooling like winget).</summary>
    public static async Task<bool> IsAvailableAsync(string fileName, string probeArgs = "--version",
        CancellationToken ct = default)
    {
        try
        {
            await RunAsync(fileName, probeArgs, ct: ct);
            return true;
        }
        catch (ToolNotFoundException) { return false; }
        catch (OperationCanceledException) { throw; }
        catch { return true; } // started but errored on the probe — the tool exists.
    }
}
