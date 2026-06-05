using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace SetupUninstaller;

// Small native (AOT) per-app uninstaller. One copy lives in each installed app folder, branded
// with that app's icon. It reads the install-log.json next to it (or a /LOG= path), removes that
// app's files / registry entry, and schedules deletion of its own folder after it exits.
internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_YESNO = 0x00000004;
    private const uint MB_OK = 0x00000000;
    private const uint MB_RETRYCANCEL = 0x00000005;
    private const uint MB_ICONQUESTION = 0x00000020;
    private const uint MB_ICONINFORMATION = 0x00000040;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_ICONWARNING = 0x00000030;
    private const int IDYES = 6;
    private const int IDRETRY = 4;

    [STAThread]
    private static int Main(string[] args)
    {
        var silent = args.Any(a =>
            a.Equals("/SILENT", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/VERYSILENT", StringComparison.OrdinalIgnoreCase));

        // Removing from Program Files + HKLM requires elevation.
        if (!IsElevated())
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath,
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")),
                });
            }
            catch { /* user declined elevation */ }
            return 0;
        }

        string? logPath = null;
        foreach (var a in args)
            if (a.StartsWith("/LOG=", StringComparison.OrdinalIgnoreCase))
                logPath = a[5..].Trim('"');
        logPath ??= Path.Combine(AppContext.BaseDirectory, "install-log.json");

        if (!silent &&
            MessageBoxW(IntPtr.Zero,
                "Are you sure you want to remove this application and all of its files?",
                "Confirm Uninstall", MB_YESNO | MB_ICONQUESTION) != IDYES)
        {
            return 0;
        }

        try
        {
            UninstallLog? log = File.Exists(logPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(logPath), UninstallJsonContext.Default.UninstallLog)
                : null;

            if (log is null)
                throw new InvalidOperationException($"Install log not found: {logPath}");

            // Don't delete anything while the app is still running, or we'd leave a half-removed
            // install (locked files survive, but the uninstaller + Control Panel entry are gone).
            // Prompt to close it and retry. In silent mode there's no one to prompt, so proceed
            // best-effort (locked deletes are tolerated).
            if (!silent)
            {
                while (true)
                {
                    var running = GetBlockingProcesses(log.AppDir);
                    if (running.Count == 0)
                        break;

                    var msg = "The following program(s) from this application are still running and "
                        + "must be closed before it can be uninstalled:\n\n  "
                        + string.Join("\n  ", running)
                        + "\n\nClose them and click Retry, or Cancel to abort.";
                    if (MessageBoxW(IntPtr.Zero, msg, "Application Is Running",
                            MB_RETRYCANCEL | MB_ICONWARNING) != IDRETRY)
                        return 0; // user cancelled — nothing removed
                }
            }

            foreach (var file in log.InstalledFiles)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }

            // Delete the log itself so the app folder can become empty.
            try { if (File.Exists(logPath)) File.Delete(logPath); } catch { }

            foreach (var dir in log.InstalledDirectories.OrderByDescending(d => d.Length))
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: false); } catch { }
            }

            if (!string.IsNullOrWhiteSpace(log.UninstallKeyName))
            {
                foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
                {
                    try
                    {
                        using var key = root.OpenSubKey(
                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
                        key?.DeleteSubKeyTree(log.UninstallKeyName, throwOnMissingSubKey: false);
                    }
                    catch { }
                }
            }

            ScheduleSelfDelete(log);

            if (!silent)
                MessageBoxW(IntPtr.Zero, "The application has been uninstalled.",
                    "Uninstall Complete", MB_OK | MB_ICONINFORMATION);
            return 0;
        }
        catch (Exception ex)
        {
            if (!silent)
                MessageBoxW(IntPtr.Zero, $"Uninstall failed: {ex.Message}",
                    "Uninstall Error", MB_OK | MB_ICONERROR);
            return 1;
        }
    }

    // The running Uninstall.exe can't delete its own folder, so hand it to a detached cmd that
    // waits for this process to exit. Removes only this app's folder, then the install root if it
    // is now empty (other apps keep their own folders, so theirs are untouched).
    private static void ScheduleSelfDelete(UninstallLog log)
    {
        if (string.IsNullOrWhiteSpace(log.AppDir))
            return;

        var cmd = $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{log.AppDir}\"";
        if (!string.IsNullOrWhiteSpace(log.RootDir))
            cmd += $" & rmdir \"{log.RootDir}\" 2>nul";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmd,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch { }
    }

    // Returns the names of running processes whose executable lives inside the app folder
    // (excluding this uninstaller itself, which also runs from there).
    private static List<string> GetBlockingProcesses(string? appDir)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(appDir))
            return names;

        var prefix = appDir.TrimEnd('\\') + "\\";
        var self = Environment.ProcessId;

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self)
                    continue;

                string? path = null;
                try { path = p.MainModule?.FileName; } catch { /* access denied / exited */ }

                if (path is not null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && !names.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(p.ProcessName);
                }
            }
            catch { }
            finally { p.Dispose(); }
        }

        return names;
    }

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

// Mirrors the InstallLog written by the bootstrapper (PascalCase JSON, no naming policy).
internal sealed class UninstallLog
{
    public string UninstallKeyName { get; set; } = string.Empty;
    public List<string> InstalledFiles { get; set; } = [];
    public List<string> InstalledDirectories { get; set; } = [];
    public string? RootDir { get; set; }
    public string? AppDir { get; set; }
    public string? SharedUninstallerPath { get; set; }
}

[JsonSerializable(typeof(UninstallLog))]
internal partial class UninstallJsonContext : JsonSerializerContext;
