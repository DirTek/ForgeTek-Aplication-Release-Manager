using System.IO;
using System.Windows;
using ForgeTekApplicationReleaseManager.Models;
using ForgeTekApplicationReleaseManager.Services;
using static ForgeTekApplicationReleaseManager.Services.LocalizationService;

namespace ForgeTekApplicationReleaseManager.Dialogs;

/// <summary>Generates a standalone, branded updater EXE for one app — a single-file applier that
/// consumes staged FTUP packages + the update plan, swaps files, and relaunches the app.</summary>
public partial class GenerateUpdaterWindow : Window
{
    private readonly IUpdaterService _updater;
    private readonly ISettingsService _settings;
    private readonly IStorageService _storage;
    private readonly AppEntry _entry;
    private readonly AppSettings _appSettings;

    // Detected launch-exe name → its full path on disk (for icon extraction).
    private readonly Dictionary<string, string> _exePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _exeNames = [];
    private bool _busy;

    public GenerateUpdaterWindow(AppEntry entry, IUpdaterService updater, ISettingsService settings,
        IStorageService storage)
    {
        InitializeComponent();
        _entry = entry;
        _updater = updater;
        _settings = settings;
        _storage = storage;
        _appSettings = settings.LoadAppSettings(entry.Name);

        SubtitleText.Text = S("Str.GenUpdaterCB.SubtitleFmt", entry.Name);

        var ext = string.IsNullOrWhiteSpace(_appSettings.PackageExtension)
            ? "ftu" : _appSettings.PackageExtension!.TrimStart('.');
        ExtensionText.Text = $".{ext}";

        PopulateExeCandidates();
        // Default to the app's own folder so the updater ships with the app and is picked up as a
        // tracked file. (Saving it into FARM's folder would be useless to the destination app.)
        OutputBox.Text = _entry.FolderPath;

        // Sign toggle only when a global signing cert is configured.
        var g = _settings.Global;
        if (g.UseStoreCert || !string.IsNullOrWhiteSpace(g.GlobalCertPath))
            SignCheck.Visibility = Visibility.Visible;
    }

    // Collects non-debug .exe files from the latest version; defaults to the best name match. Any are
    // selectable from the dropdown, and Browse… adds one the scan didn't pick up.
    private void PopulateExeCandidates()
    {
        var version = _entry.Versions
            .Where(v => v.Status != VersionStatus.Retracted && v.Status != VersionStatus.Scrapped)
            .LastOrDefault();

        if (version is not null)
        {
            foreach (var f in version.Files)
            {
                if (f.IsDebug) continue;
                if (!".exe".Equals(Path.GetExtension(f.Path), StringComparison.OrdinalIgnoreCase)) continue;

                var name = Path.GetFileName(f.Path);
                if (!_exePaths.ContainsKey(name))
                {
                    _exePaths[name] = Path.Combine(_entry.FolderPath, f.Path);
                    _exeNames.Add(name);
                }
            }
        }

        // Prefer an exe whose name resembles the app name; else the first; else a sensible guess.
        var compact = new string(_entry.Name.Where(char.IsLetterOrDigit).ToArray());
        var best = _exeNames.FirstOrDefault(n =>
                       new string(Path.GetFileNameWithoutExtension(n).Where(char.IsLetterOrDigit).ToArray())
                           .Equals(compact, StringComparison.OrdinalIgnoreCase))
                   ?? _exeNames.FirstOrDefault()
                   ?? $"{compact}.exe";

        if (!_exeNames.Contains(best, StringComparer.OrdinalIgnoreCase))
            _exeNames.Add(best);

        RefreshExeItems(best);
    }

    // Rebinds the dropdown items and selects the given name.
    private void RefreshExeItems(string select)
    {
        AppExeBox.ItemsSource = null;
        AppExeBox.ItemsSource = _exeNames;
        AppExeBox.SelectedItem = _exeNames.FirstOrDefault(n => n.Equals(select, StringComparison.OrdinalIgnoreCase));
    }

    private void AppExeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => SyncIconDefault();

    // Lets the user point at the app's exe directly (e.g. when the scan didn't capture it). We store the
    // file NAME (the updater lives beside the app), and remember the full path for icon extraction.
    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = S("Str.GenUpdaterCB.PickExeFile"),
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
        };
        if (Directory.Exists(_entry.FolderPath)) dlg.InitialDirectory = _entry.FolderPath;
        if (dlg.ShowDialog() != true) return;

        var name = Path.GetFileName(dlg.FileName);
        _exePaths[name] = dlg.FileName;
        if (!_exeNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            _exeNames.Add(name);
        RefreshExeItems(name);
    }

    // Defaults the icon source to the chosen launch exe's full path (when it exists on disk).
    private void SyncIconDefault()
    {
        if (!string.IsNullOrWhiteSpace(IconBox.Text) && File.Exists(IconBox.Text)) return;
        var name = (AppExeBox.SelectedItem as string)?.Trim() ?? string.Empty;
        if (_exePaths.TryGetValue(name, out var full) && File.Exists(full))
            IconBox.Text = full;
    }

    private void BrowseIcon_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = S("Str.GenUpdaterCB.PickIcon"),
            Filter = "Icon or executable (*.ico;*.exe)|*.ico;*.exe|All files (*.*)|*.*",
        };
        if (!string.IsNullOrWhiteSpace(IconBox.Text))
            try { dlg.InitialDirectory = Path.GetDirectoryName(IconBox.Text); } catch { }
        if (dlg.ShowDialog() == true) IconBox.Text = dlg.FileName;
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = S("Str.GenUpdaterCB.PickOutput") };
        if (!string.IsNullOrWhiteSpace(OutputBox.Text)) dlg.InitialDirectory = OutputBox.Text;
        if (dlg.ShowDialog() == true) OutputBox.Text = dlg.FolderName;
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var appExe = (AppExeBox.SelectedItem as string)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(appExe))
        {
            StatusText.Text = S("Str.GenUpdaterCB.PickExe");
            return;
        }
        if (!appExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            appExe += ".exe";

        var outputFolder = OutputBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            StatusText.Text = S("Str.GenUpdaterCB.PickOutput");
            return;
        }

        var icon = IconBox.Text?.Trim();
        var options = new UpdaterGenOptions
        {
            AppExeName = appExe,
            IconSourcePath = string.IsNullOrWhiteSpace(icon) ? null : icon,
            OutputFolder = outputFolder,
            Sign = SignCheck.Visibility == Visibility.Visible && SignCheck.IsChecked == true,
        };

        _busy = true;
        GenerateBtn.IsEnabled = false;
        ResultPanel.Visibility = Visibility.Collapsed;
        LogText.Text = string.Empty;
        GenBar.Visibility = Visibility.Visible;
        GenBar.IsIndeterminate = true;
        StatusText.Text = S("Str.GenUpdaterCB.Generating");

        var progress = new Progress<string>(Append);
        try
        {
            var record = await Task.Run(() => _updater.GenerateAsync(_entry, _appSettings, options, progress));
            ResultBox.Text = record.OutputPath;
            ResultPanel.Visibility = Visibility.Visible;

            // Track the produced files (exe + sidecar) in the app's scanned files when they live inside
            // the app folder, so they ship with the app and are included in packages.
            var tracked = RegisterScannedFiles(record);
            if (tracked > 0) Append(S("Str.GenUpdaterCB.AddedToScanFmt", tracked));

            StatusText.Text = record.Branded
                ? S("Str.GenUpdaterCB.DoneBranded")
                : S("Str.GenUpdaterCB.DonePrebuilt");
        }
        catch (Exception ex)
        {
            StatusText.Text = S("Str.GenUpdaterCB.FailedFmt", ex.Message);
            Append($"ERROR: {ex.Message}");
        }
        finally
        {
            _busy = false;
            GenerateBtn.IsEnabled = true;
            GenBar.IsIndeterminate = false;
            GenBar.Visibility = Visibility.Collapsed;
        }
    }

    // Adds the generated updater EXE + sidecar to the latest version's scanned files (when they sit
    // inside the app folder), replacing any prior entry for the same path. Returns how many were added.
    private int RegisterScannedFiles(GeneratedUpdaterRecord record)
    {
        var version = _entry.Versions
            .Where(v => v.Status != VersionStatus.Retracted && v.Status != VersionStatus.Scrapped)
            .LastOrDefault();
        if (version is null) return 0;

        var root = Path.GetFullPath(_entry.FolderPath);
        var added = 0;
        foreach (var path in new[] { record.OutputPath, record.SidecarPath })
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

            var full = Path.GetFullPath(path);
            // Only track files that actually live under the app folder (relative paths must be valid).
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;

            var rel = Path.GetRelativePath(root, full);
            version.Files.RemoveAll(f => string.Equals(f.Path, rel, StringComparison.OrdinalIgnoreCase));
            version.Files.Add(new FileRecord
            {
                Path = rel,
                Checksum = ScannerService.ComputeChecksum(full),
                DateModified = File.GetLastWriteTime(full),
                IsDebug = false,
            });
            added++;
        }

        if (added > 0) _storage.Update(_entry);
        return added;
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ResultBox.Text) || !File.Exists(ResultBox.Text)) return;
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{ResultBox.Text}\""); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Append(string line)
    {
        LogText.Text = string.IsNullOrEmpty(LogText.Text) ? line : LogText.Text + "\n" + line;
        LogScroller.ScrollToEnd();
    }
}
