using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace SetupBootstrapper;

public partial class App : System.Windows.Application
{
    public static string[]? CliArgs { get; private set; }
    public static bool IsSilent => CliArgs is not null && (
        CliArgs.Contains("/SILENT", StringComparer.OrdinalIgnoreCase) ||
        CliArgs.Contains("/VERYSILENT", StringComparer.OrdinalIgnoreCase));

    public static bool IsUninstall => CliArgs is not null &&
        CliArgs.Contains("/UNINSTALL", StringComparer.OrdinalIgnoreCase);

    public static string? CustomInstallDir { get; private set; }

    private static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        CliArgs = e.Args;
        if (e.Args.Length > 0)
        {
            for (int i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i].StartsWith("/DIR=", StringComparison.OrdinalIgnoreCase))
                {
                    CustomInstallDir = e.Args[i][5..];
                }
            }
        }

        // Self-elevate if not already admin
        if (!IsElevated)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(" ", e.Args.Select(a => $"\"{a}\"")),
            };
            try
            {
                Process.Start(psi);
            }
            catch
            {
                System.Windows.MessageBox.Show(
                    "This setup requires administrator privileges to install to Program Files.",
                    "Elevation Required",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
            Shutdown();
            return;
        }

        if (IsUninstall)
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            var result = System.Windows.MessageBox.Show(
                "Are you sure you want to remove this application and all its files?",
                "Confirm Uninstall",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (result == System.Windows.MessageBoxResult.Yes)
                PerformUninstall(showMessages: true);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    internal static void PerformUninstall(bool showMessages = true)
    {
        try
        {
            var exePath = Environment.ProcessPath!;
            var installDir = Path.GetDirectoryName(exePath)!;

            // Locate the install log: an explicit /LOG=<path> (per-app Control Panel entries) or,
            // for legacy single-entry installs, install-log.json next to this exe.
            string? logPath = null;
            if (CliArgs is not null)
            {
                foreach (var a in CliArgs)
                    if (a.StartsWith("/LOG=", StringComparison.OrdinalIgnoreCase))
                        logPath = a[5..].Trim('"');
            }
            logPath ??= Path.Combine(installDir, "install-log.json");

            InstallLog? log = null;
            if (File.Exists(logPath))
            {
                log = JsonSerializer.Deserialize<InstallLog>(File.ReadAllText(logPath));
            }

            if (log is not null)
            {
                foreach (var file in log.InstalledFiles)
                {
                    try { if (File.Exists(file)) File.Delete(file); }
                    catch { }
                }
                // Delete the log itself so its app directory can become empty and be removed.
                try { if (File.Exists(logPath)) File.Delete(logPath); } catch { }

                foreach (var dir in log.InstalledDirectories.OrderByDescending(d => d.Length))
                {
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: false); }
                    catch { }
                }
            }
            else
            {
                try { Directory.Delete(installDir, recursive: true); }
                catch { }
            }

            if (log is not null && !string.IsNullOrWhiteSpace(log.UninstallKeyName))
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
                    key?.DeleteSubKeyTree(log.UninstallKeyName, throwOnMissingSubKey: false);
                }
                catch { }

                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
                    key?.DeleteSubKeyTree(log.UninstallKeyName, throwOnMissingSubKey: false);
                }
                catch { }
            }

            // Remove any "run as admin" AppCompatFlags Layers values this app added.
            if (log is { LayersEntries.Count: > 0 })
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", writable: true);
                    if (k is not null)
                        foreach (var v in log.LayersEntries)
                            try { k.DeleteValue(v, throwOnMissingValue: false); } catch { }
                }
                catch { }
            }

            // If this was the last app sharing the install root, remove the shared Uninstall.exe
            // (the running process) and the now-empty root after we exit.
            if (log is not null && !string.IsNullOrWhiteSpace(log.RootDir)
                && !string.IsNullOrWhiteSpace(log.SharedUninstallerPath))
            {
                CleanupSharedRootIfLastApp(log.RootDir!, log.SharedUninstallerPath!);
            }
            // Legacy single-entry installs kept Uninstall.exe + install-log.json in an app folder.
            else if (log is not null && log.InstalledDirectories.Count > 0)
            {
                var rootDirOld = log.InstalledDirectories[0];
                try { if (File.Exists(Path.Combine(rootDirOld, "Uninstall.exe"))) File.Delete(Path.Combine(rootDirOld, "Uninstall.exe")); } catch { }
                try { if (File.Exists(Path.Combine(rootDirOld, "install-log.json"))) File.Delete(Path.Combine(rootDirOld, "install-log.json")); } catch { }
            }

            if (showMessages)
            {
                System.Windows.MessageBox.Show(
                    "Application has been uninstalled.",
                    "Uninstall Complete",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            if (showMessages)
            {
                System.Windows.MessageBox.Show(
                    $"Uninstall failed: {ex.Message}",
                    "Uninstall Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
    }

    // Removes the shared Uninstall.exe and the install root once no other app remains under it.
    // Other apps each keep an install-log.json in their own subfolder, so their presence is the
    // signal that the root must stay. The shared exe is the running process and can't delete
    // itself, so we hand the deletion to a detached cmd that waits for us to exit.
    private static void CleanupSharedRootIfLastApp(string rootDir, string sharedUninstaller)
    {
        try
        {
            if (!Directory.Exists(rootDir))
                return;

            var anotherAppRemains = Directory.EnumerateDirectories(rootDir)
                .Any(d => File.Exists(Path.Combine(d, "install-log.json")));
            if (anotherAppRemains)
                return;

            // Delete only the shared uninstaller, then remove the root ONLY if it's empty
            // (plain rmdir, no /s). This preserves any other app's leftover files at the root.
            var args = $"/c timeout /t 2 /nobreak >nul & del /f /q \"{sharedUninstaller}\" & rmdir \"{rootDir}\" 2>nul";
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
        }
        catch { }
    }
}

internal sealed class InstallLog
{
    public string UninstallKeyName { get; set; } = string.Empty;
    public List<string> InstalledFiles { get; set; } = [];
    public List<string> InstalledDirectories { get; set; } = [];

    // Per-app entries (multi-app setups): the shared install root, this app's folder, and the
    // path to the shared Uninstall.exe. Null for legacy single-entry installs.
    public string? RootDir { get; set; }
    public string? AppDir { get; set; }
    public string? SharedUninstallerPath { get; set; }
    public List<string> LayersEntries { get; set; } = [];
}
