using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace SetupBootstrapper;

public partial class MainWindow : Window
{
    private static readonly byte[] EndMagic = "STUPEND"u8.ToArray();

    // Folder inside the package (and extracted temp dir) holding the staged per-app uninstaller.
    private const string UninstallerStageDir = "__uninstaller__";

    private enum PagePhase { Eula, Path, Prereq }

    private InstallManifest? _manifest;
    private string _tempDir = string.Empty;
    private bool _installed;
    private PagePhase _currentPage = PagePhase.Eula;

    // Maintenance (Repair/Remove) checkbox rows: the checkbox, its app, and its install dir.
    private readonly List<(System.Windows.Controls.CheckBox Check, InstallApp App, string Dir)> _maintItems = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var exePath = Environment.ProcessPath!;

            _tempDir = Path.Combine(Path.GetTempPath(), "ForgeTekSetup",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            // Diagnostic: log file info before extraction
            try
            {
                var diagLog = Path.Combine(Path.GetTempPath(), "ForgeTekBootstrapDiag.txt");
                var info = new FileInfo(exePath);
                using var diagFs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                diagFs.Seek(-Math.Min(20, diagFs.Length), SeekOrigin.End);
                var tail = new byte[Math.Min(20, diagFs.Length)];
                diagFs.ReadExactly(tail);
                File.WriteAllText(diagLog,
                    $"Path: {exePath}\r\nSize: {info.Length}\r\nLast {tail.Length} bytes: {BitConverter.ToString(tail)}\r\n");
            }
            catch { }

            // Extract on background thread so UI renders immediately
            _manifest = await Task.Run(() =>
            {
                ExtractZipFromSelf(exePath, _tempDir);

                var manifestPath = Path.Combine(_tempDir, "install.json");
                if (!File.Exists(manifestPath))
                    throw new InvalidDataException("install.json not found in package.");

                return JsonSerializer.Deserialize<InstallManifest>(
                    File.ReadAllText(manifestPath),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
            });

            Title = $"Setup — {_manifest.SetupName} v{_manifest.SetupVersion}";
            TitleText.Text = _manifest.SetupName;
            SubtitleText.Text = $"Version {_manifest.SetupVersion}";

            try
            {
                using var assocIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (assocIcon is not null)
                    Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        assocIcon.Handle,
                        System.Windows.Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            }
            catch { }

            var company = string.IsNullOrWhiteSpace(_manifest.CompanyName) ? "ForgeTek" : _manifest.CompanyName;
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var rootDir = App.CustomInstallDir ?? Path.Combine(programFiles, company);
            InstallPathBox.Text = rootDir;
            DefaultPathText.Text = $"Default: {rootDir}";

            var appItems = _manifest.Apps.Select(a => new InstallAppItem
            {
                Name = a.Name,
                SubPath = Path.Combine(rootDir, a.DefaultInstallDir),
                CreateShortcut = a.CreateShortcut,
            }).ToList();
            AppList.ItemsSource = appItems;

            // Hide loading, show real EULA content
            EulaLoadingText.Visibility = Visibility.Collapsed;
            EulaContent.Visibility = Visibility.Visible;

            // Set EULA text
            EulaBody.Text = string.IsNullOrWhiteSpace(_manifest.EulaText)
                ? "END USER LICENSE AGREEMENT\n\nThe publisher of this software has not provided a license agreement for this setup."
                : _manifest.EulaText;

            // Load banner image if present
            if (!string.IsNullOrWhiteSpace(_manifest.BannerImageName))
            {
                var bannerPath = Path.Combine(_tempDir, _manifest.BannerImageName);
                if (File.Exists(bannerPath))
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(bannerPath, UriKind.Absolute);
                    bitmap.EndInit();
                    BannerImage.Source = bitmap;
                    BannerBorder.Visibility = Visibility.Visible;
                }
            }

            // Apply the bundle's custom window appearance (background color/gradient/image, size).
            ApplyAppearance();

            // If this bundle is already installed, offer Repair/Remove instead of re-installing.
            var installedApps = GetInstalledApps();
            if (installedApps.Count > 0 && !App.IsSilent)
            {
                ShowMaintenancePage(installedApps);
                return;
            }

            if (App.IsSilent)
            {
                InstallPathBox.Text = App.CustomInstallDir ?? rootDir;
                PageEula.Visibility = Visibility.Collapsed;
                PagePath.Visibility = Visibility.Visible;
                #pragma warning disable CS4014
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(DoInstall));
                #pragma warning restore CS4014
            }
            else
            {
                _currentPage = PagePhase.Eula;
                ActionBtn.IsEnabled = false;
            }
        }
        catch (InvalidDataException)
        {
            // No ZIP package data — likely the bootstrapper-only copy used as Uninstall.exe
            ShowUninstallPage();
        }
        catch (Exception ex)
        {
            ShowError($"Failed to initialize setup: {ex.Message}");
        }
    }

    private void EulaAcceptCheck_Changed(object sender, RoutedEventArgs e)
    {
        ActionBtn.IsEnabled = EulaAcceptCheck.IsChecked == true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            SelectedPath = InstallPathBox.Text,
            Description = "Select installation directory"
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            InstallPathBox.Text = dlg.SelectedPath;
    }

    private void ActionBtn_Click(object sender, RoutedEventArgs e)
    {
        switch (_currentPage)
        {
            case PagePhase.Eula:
                ShowPathPage();
                break;
            case PagePhase.Path:
                ShowPrereqPage();
                break;
            case PagePhase.Prereq:
                DoInstall();
                break;
        }
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        switch (_currentPage)
        {
            case PagePhase.Path:
                ShowEulaPage();
                break;
            case PagePhase.Prereq:
                ShowPathPage();
                break;
        }
    }

    private void ShowEulaPage()
    {
        _currentPage = PagePhase.Eula;
        PagePath.Visibility = Visibility.Collapsed;
        PagePrereq.Visibility = Visibility.Collapsed;
        PageRedist.Visibility = Visibility.Collapsed;
        PageEula.Visibility = Visibility.Visible;
        ActionBtn.Content = "Next";
        ActionBtn.IsEnabled = EulaAcceptCheck.IsChecked == true;
        BackBtn.Visibility = Visibility.Collapsed;
    }

    private void ShowPathPage()
    {
        _currentPage = PagePhase.Path;
        PageEula.Visibility = Visibility.Collapsed;
        PagePrereq.Visibility = Visibility.Collapsed;
        PageRedist.Visibility = Visibility.Collapsed;
        PagePath.Visibility = Visibility.Visible;
        ActionBtn.Content = "Install";
        ActionBtn.IsEnabled = true;
        BackBtn.Visibility = Visibility.Visible;
    }

    private void ShowPrereqPage()
    {
        _currentPage = PagePhase.Prereq;
        PagePath.Visibility = Visibility.Collapsed;
        PageRedist.Visibility = Visibility.Collapsed;
        PagePrereq.Visibility = Visibility.Visible;
        ActionBtn.Content = "Continue";
        ActionBtn.IsEnabled = true;
        BackBtn.Visibility = Visibility.Visible;

        // Run detection
        var results = new ObservableCollection<RedistDetectionResult>();
        PrereqList.ItemsSource = results;

        if (_manifest is null || _manifest.Redists.Count == 0)
        {
            PrereqSummaryText.Text = "No prerequisites required.";
            return;
        }

        int detected = 0;
        foreach (var redist in _manifest.Redists)
        {
            var isDetected = CheckRegistryRedist(redist);
            if (isDetected) detected++;
            results.Add(new RedistDetectionResult
            {
                Name = redist.Name,
                IsDetected = isDetected,
            });
        }

        if (detected == _manifest.Redists.Count)
            PrereqSummaryText.Text = "✔ All prerequisites are already installed.";
        else
            PrereqSummaryText.Text = $"⚠ {_manifest.Redists.Count - detected} prerequisite(s) need to be installed.";
    }

    private async void DoInstall()
    {
        if (_manifest is null || _installed) return;

        PagePrereq.Visibility = Visibility.Collapsed;
        ActionBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;
        BackBtn.Visibility = Visibility.Collapsed;

        var rootDir = InstallPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(rootDir))
        {
            ShowError("Please select an installation directory.");
            return;
        }

        // Capture shortcut preferences from the Path page
        var shortcutRequests = new List<(string Name, string InstallDir, bool Create)>();
        if (AppList.ItemsSource is IList<InstallAppItem> items)
        {
            foreach (var item in items)
            {
                var dir = Path.Combine(rootDir,
                    _manifest?.Apps.FirstOrDefault(a => a.Name == item.Name)?.DefaultInstallDir ?? item.Name);
                shortcutRequests.Add((item.Name, dir, item.CreateShortcut));
            }
        }

        try
        {
            // Step 1: Install redistributables
            if (_manifest is not null && _manifest.Redists.Count > 0)
            {
                var redistDir = Path.Combine(_tempDir, "redist");
                if (Directory.Exists(redistDir))
                {
                    PageRedist.Visibility = Visibility.Visible;

                    for (int i = 0; i < _manifest.Redists.Count; i++)
                    {
                        var redist = _manifest.Redists[i];

                        if (!string.IsNullOrWhiteSpace(redist.DetectionKeyPath) &&
                            !string.IsNullOrWhiteSpace(redist.DetectionValueName))
                        {
                            if (CheckRegistryRedist(redist))
                            {
                                RedistFileText.Text = $"{redist.Name} — already installed, skipping";
                                RedistProgress.Value = (double)(i + 1) / _manifest.Redists.Count * 100;
                                await Task.Yield();
                                continue;
                            }
                        }

                        RedistStatus.Text = $"Installing {redist.Name}… ({i + 1}/{_manifest.Redists.Count})";
                        RedistFileText.Text = redist.Name;

                        var redistExe = Path.Combine(redistDir, redist.ExeName);
                        if (File.Exists(redistExe))
                        {
                            var psi = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = redistExe,
                                Arguments = redist.Arguments,
                                UseShellExecute = true,
                                Verb = "runas",
                            };

                            using var proc = System.Diagnostics.Process.Start(psi);
                            if (proc is not null)
                                proc.WaitForExit();
                        }

                        RedistProgress.Value = (double)(i + 1) / _manifest.Redists.Count * 100;
                        await Task.Yield();
                    }

                    PageRedist.Visibility = Visibility.Collapsed;
                }
            }

            // Step 2: Install app files. The copy loop runs on a background thread and reports
            // progress via Progress<T> (marshalled back to the UI thread). Doing the work on the
            // UI thread starves WPF's Render priority, so the progress bar never visibly moves.
            PageProgress.Visibility = Visibility.Visible;

            // Force layout to render the progress page before starting file copies
            PageProgress.UpdateLayout();

            var appsDir = Path.Combine(_tempDir, "apps");

            // Track installed files/dirs per app (keyed by DefaultInstallDir) so each app can
            // get its own Control Panel entry + uninstall log.
            var appFiles = new Dictionary<string, List<string>>();
            var appDirs = new Dictionary<string, List<string>>();

            var installProgress = new Progress<(double Pct, string File, string Status)>(p =>
            {
                InstallProgress.Value = p.Pct;
                ProgressFileText.Text = p.File;
                ProgressStatus.Text = p.Status;
            });

            await Task.Run(() =>
            {
                if (!Directory.Exists(appsDir))
                    return;

                IProgress<(double, string, string)> report = installProgress;
                var totalFiles = Directory.GetFiles(appsDir, "*", SearchOption.AllDirectories).Length;
                var processed = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();

                foreach (var app in _manifest!.Apps)
                {
                    var sourceAppDir = Path.Combine(appsDir, app.DefaultInstallDir);
                    var targetAppDir = Path.Combine(rootDir, app.DefaultInstallDir);

                    if (!Directory.Exists(sourceAppDir)) continue;

                    var files = new List<string>();
                    var dirs = new List<string> { targetAppDir };
                    appFiles[app.DefaultInstallDir] = files;
                    appDirs[app.DefaultInstallDir] = dirs;

                    Directory.CreateDirectory(targetAppDir);

                    foreach (var file in Directory.GetFiles(sourceAppDir, "*", SearchOption.AllDirectories))
                    {
                        var relPath = Path.GetRelativePath(sourceAppDir, file);
                        var targetFile = Path.Combine(targetAppDir, relPath);
                        var targetDir = Path.GetDirectoryName(targetFile)!;

                        Directory.CreateDirectory(targetDir);
                        if (!dirs.Contains(targetDir))
                            dirs.Add(targetDir);

                        using (var src = File.OpenRead(file))
                        using (var dst = File.Create(targetFile))
                            src.CopyTo(dst);

                        files.Add(targetFile);

                        processed++;
                        // Throttle UI updates (~30 fps) so the dispatcher isn't flooded, but
                        // always report the final file so the bar lands on 100%.
                        if (sw.ElapsedMilliseconds >= 33 || processed == totalFiles)
                        {
                            sw.Restart();
                            report.Report(((double)processed / totalFiles * 100,
                                $"{app.Name}\\{relPath}",
                                $"Installing {app.Name}… ({processed}/{totalFiles})"));
                        }
                    }
                }

                // Grant Users Modify permission to all app directories (recursive)
                foreach (var app in _manifest.Apps)
                {
                    var appDir = Path.Combine(rootDir, app.DefaultInstallDir);
                    if (Directory.Exists(appDir))
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "icacls",
                            Arguments = $"\"{appDir}\" /grant \"Users:(OI)(CI)M\" /T /Q",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };
                        using var proc = System.Diagnostics.Process.Start(psi);
                        proc?.WaitForExit();
                    }
                }
            });

            // Step 2b: Write per-app registry entries
            foreach (var app in _manifest!.Apps)
            {
                var appDir = Path.Combine(rootDir, app.DefaultInstallDir);
                foreach (var reg in app.RegistryEntries)
                {
                    try
                    {
                        var hive = reg.Root switch
                        {
                            "HKLM" => Microsoft.Win32.Registry.LocalMachine,
                            "HKCR" => Microsoft.Win32.Registry.ClassesRoot,
                            _ => Microsoft.Win32.Registry.CurrentUser,
                        };

                        var resolvedData = reg.ValueData.Replace("[InstallDir]", appDir);

                        using var key = hive.CreateSubKey(reg.KeyPath);
                        if (key is not null)
                        {
                            var kind = reg.ValueKind switch
                            {
                                "DWord" => Microsoft.Win32.RegistryValueKind.DWord,
                                "QWord" => Microsoft.Win32.RegistryValueKind.QWord,
                                "ExpandString" => Microsoft.Win32.RegistryValueKind.ExpandString,
                                _ => Microsoft.Win32.RegistryValueKind.String,
                            };

                            if (kind == Microsoft.Win32.RegistryValueKind.DWord &&
                                uint.TryParse(resolvedData, out var dword))
                                key.SetValue(reg.ValueName, dword, kind);
                            else if (kind == Microsoft.Win32.RegistryValueKind.QWord &&
                                     ulong.TryParse(resolvedData, out var qword))
                                key.SetValue(reg.ValueName, qword, kind);
                            else
                                key.SetValue(reg.ValueName, resolvedData, kind);
                        }
                    }
                    catch { }
                }
            }

            // Step 3: Decide the uninstaller strategy.
            //  • Preferred: a small per-app Uninstall.exe (the AOT SetupUninstaller staged in the
            //    package) copied into each app folder and branded with that app's icon.
            //  • Fallback (package has no staged uninstaller — e.g. built before the AOT tools were
            //    available): ONE shared Uninstall.exe in the root (bootstrapper minus its ZIP),
            //    invoked per app via /LOG=.
            var selfPath = Environment.ProcessPath!;
            var publisher = string.IsNullOrWhiteSpace(_manifest.CompanyName) ? "ForgeTek" : _manifest.CompanyName;

            var stagedUninstaller = Path.Combine(_tempDir, UninstallerStageDir, "SetupUninstaller.exe");
            var perAppUninstaller = File.Exists(stagedUninstaller);

            string? sharedUninstallExe = null;
            if (!perAppUninstaller)
            {
                sharedUninstallExe = Path.Combine(rootDir, "Uninstall.exe");
                try
                {
                    long bootstrapperLen;
                    using var fs = new FileStream(selfPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (fs.Length >= EndMagic.Length + 8)
                    {
                        try { bootstrapperLen = FindZipFooter(selfPath).ZipOffset; }
                        catch (InvalidDataException) { bootstrapperLen = fs.Length; }
                    }
                    else
                        bootstrapperLen = fs.Length;

                    fs.Position = 0;
                    using var dst = File.Create(sharedUninstallExe);
                    var buffer = new byte[65536];
                    long remaining = bootstrapperLen;
                    while (remaining > 0)
                    {
                        var read = fs.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                        if (read == 0) break;
                        dst.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
                catch { }
            }

            // Step 4: One Control Panel entry + one uninstall log per app.
            foreach (var app in _manifest.Apps)
            {
                if (!appFiles.TryGetValue(app.DefaultInstallDir, out var files))
                    continue;
                var dirs = appDirs[app.DefaultInstallDir];
                var appDir = Path.Combine(rootDir, app.DefaultInstallDir);
                var appKeyName = Sanitize($"{_manifest.SetupName}_{app.Name}");
                var appLogPath = Path.Combine(appDir, "install-log.json");

                // The icon to show for this app: its chosen icon / launch exe (an installed file).
                var appIconSource = ResolveInstalledIcon(appDir, app);

                string uninstallString;
                string displayIcon;

                if (perAppUninstaller)
                {
                    // Copy the small uninstaller into the app folder and brand it with the app's
                    // icon (safe: it's a native PE, not the single-file bootstrapper).
                    var appUninstallExe = Path.Combine(appDir, "Uninstall.exe");
                    try { File.Copy(stagedUninstaller, appUninstallExe, overwrite: true); } catch { }

                    if (appIconSource is not null)
                    {
                        if (".ico".Equals(Path.GetExtension(appIconSource), StringComparison.OrdinalIgnoreCase))
                            NativeIconInjector.InjectFromIco(appIconSource, appUninstallExe);
                        else
                            NativeIconInjector.InjectFromExe(appIconSource, appUninstallExe);
                    }

                    uninstallString = $"\"{appUninstallExe}\" /UNINSTALL";
                    displayIcon = appUninstallExe; // now carries the app's own icon
                }
                else
                {
                    uninstallString = $"\"{sharedUninstallExe}\" /UNINSTALL /LOG=\"{appLogPath}\"";
                    displayIcon = appIconSource ?? sharedUninstallExe!;
                }

                // Flag chosen exes "always run as administrator" via the AppCompatFlags Layers key
                // (HKLM — applies to all users; the setup is elevated). Recorded for uninstall cleanup.
                var layersEntries = new List<string>();
                foreach (var exe in app.RunAsAdminExes)
                {
                    var exePath = FindNamedExe(appDir, exe);
                    if (exePath is null) continue;
                    try
                    {
                        using var key = Registry.LocalMachine.CreateSubKey(
                            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");
                        key.SetValue(exePath, "~ RUNASADMIN", RegistryValueKind.String);
                        layersEntries.Add(exePath);
                    }
                    catch { }
                }

                var appLog = new InstallLog
                {
                    UninstallKeyName = appKeyName,
                    InstalledFiles = files,
                    InstalledDirectories = dirs,
                    RootDir = rootDir,
                    AppDir = appDir,
                    SharedUninstallerPath = sharedUninstallExe,
                    LayersEntries = layersEntries,
                };

                // Track desktop shortcut for uninstall cleanup
                var shortcutReq = shortcutRequests.FirstOrDefault(s => s.Name == app.Name);
                if (shortcutReq.Create)
                {
                    var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    appLog.InstalledFiles.Add(Path.Combine(desktop, $"{app.Name}.lnk"));
                }
                try
                {
                    File.WriteAllText(appLogPath,
                        JsonSerializer.Serialize(appLog, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch { }

                long sizeKb = 0;
                try
                {
                    foreach (var f in files)
                    {
                        var fi = new FileInfo(f);
                        if (fi.Exists) sizeKb += fi.Length;
                    }
                    sizeKb /= 1024;
                }
                catch { }

                try
                {
                    using var key = Registry.LocalMachine.CreateSubKey(
                        $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{appKeyName}");
                    key.SetValue("DisplayName", app.Name);
                    key.SetValue("DisplayVersion", _manifest.SetupVersion);
                    key.SetValue("Publisher", publisher);
                    key.SetValue("InstallLocation", appDir);
                    key.SetValue("UninstallString", uninstallString);
                    key.SetValue("DisplayIcon", displayIcon);
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    key.SetValue("EstimatedSize", (int)Math.Min(sizeKb, int.MaxValue), RegistryValueKind.DWord);
                }
                catch { }
            }

            // Remove any stray single-entry artifacts from older installs at the root.
            try { if (File.Exists(Path.Combine(rootDir, "install-log.json"))) File.Delete(Path.Combine(rootDir, "install-log.json")); } catch { }

            // Step 5: Create desktop shortcuts
            foreach (var (name, dir, create) in shortcutRequests)
            {
                if (!create || string.IsNullOrWhiteSpace(name))
                    continue;

                try
                {
                    var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    var lnkPath = Path.Combine(desktop, $"{name}.lnk");

                    var exeFiles = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).ToList();
                    var targetExe = exeFiles.FirstOrDefault(e =>
                        Path.GetFileNameWithoutExtension(e).Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? exeFiles.FirstOrDefault();

                    if (targetExe is null)
                        continue;

                    var shellType = Type.GetTypeFromCLSID(new Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8"))!;
                    dynamic shell = Activator.CreateInstance(shellType)!;
                    try
                    {
                        dynamic lnk = shell.CreateShortcut(lnkPath);
                        lnk.TargetPath = targetExe;
                        lnk.WorkingDirectory = dir;
                        lnk.Description = name;
                        lnk.Save();
                    }
                    finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
                }
                catch { }
            }

            ProgressStatus.Text = "Installation complete!";
            InstallProgress.Value = 100;
            ProgressFileText.Text = string.Empty;

            ShowCompletePage(rootDir);
        }
        catch (Exception ex)
        {
            ShowError($"Installation failed: {ex.Message}");
        }
    }

    private static bool CheckRegistryRedist(RedistInfo redist)
    {
        if (redist.DetectionKeyPath.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase))
        {
            var folderName = redist.DetectionKeyPath[5..];
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "shared");
            var dirPath = Path.Combine(baseDir, folderName);
            if (!Directory.Exists(dirPath)) return false;

            if (string.IsNullOrWhiteSpace(redist.DetectionValueName))
                return true;

            // Check if any subdirectory starts with the version prefix
            return Directory.GetDirectories(dirPath)
                .Any(d => Path.GetFileName(d).StartsWith(redist.DetectionValueName));
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(redist.DetectionKeyPath);
            if (key is null) return false;

            var value = key.GetValue(redist.DetectionValueName)?.ToString();
            if (string.IsNullOrWhiteSpace(redist.DetectionExpectedValue))
                return !string.IsNullOrWhiteSpace(value);

            return string.Equals(value, redist.DetectionExpectedValue, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void ShowCompletePage(string installDir)
    {
        _installed = true;
        PageProgress.Visibility = Visibility.Collapsed;
        PageComplete.Visibility = Visibility.Visible;
        CancelBtn.Visibility = Visibility.Collapsed;
        BackBtn.Visibility = Visibility.Collapsed;
        ActionBtn.Visibility = Visibility.Collapsed;
        FinishBtn.Visibility = Visibility.Visible;

        var appNames = string.Join(", ", _manifest!.Apps.Select(a => a.Name));
        CompleteText.Text = $"{_manifest.SetupName} has been installed to:\n{installDir}\n\n{appNames}";

        // Offer to launch the bundle's chosen app (if any) on the final page.
        if (ResolveLaunchExe(installDir) is not null)
        {
            var launchName = !string.IsNullOrWhiteSpace(_manifest.LaunchAppName)
                ? _manifest.LaunchAppName
                : _manifest.Apps.FirstOrDefault()?.Name;
            LaunchCheckbox.Content = string.IsNullOrWhiteSpace(launchName)
                ? "Launch after closing"
                : $"Launch {launchName} after closing";
            LaunchCheckbox.Visibility = Visibility.Visible;
        }
        else
        {
            LaunchCheckbox.Visibility = Visibility.Collapsed;
        }
    }

    // Applies the bundle's custom window background (solid / gradient / image) and fixed size.
    private void ApplyAppearance()
    {
        if (_manifest is null)
            return;

        try
        {
            switch (_manifest.BackgroundMode)
            {
                case "Image" when !string.IsNullOrWhiteSpace(_manifest.BackgroundImageName):
                {
                    var imgPath = Path.Combine(_tempDir, _manifest.BackgroundImageName);
                    if (File.Exists(imgPath))
                    {
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(imgPath, UriKind.Absolute);
                        bmp.EndInit();
                        Background = new System.Windows.Media.ImageBrush(bmp)
                        {
                            Stretch = System.Windows.Media.Stretch.UniformToFill,
                        };
                    }
                    break;
                }
                case "Gradient":
                {
                    if (ParseColor(_manifest.BackgroundColor1) is { } a &&
                        ParseColor(_manifest.BackgroundColor2) is { } b)
                    {
                        var (sp, ep) = _manifest.BackgroundGradientDirection switch
                        {
                            "Horizontal" => (new System.Windows.Point(0, 0.5), new System.Windows.Point(1, 0.5)),
                            "Diagonal" => (new System.Windows.Point(0, 0), new System.Windows.Point(1, 1)),
                            _ => (new System.Windows.Point(0.5, 0), new System.Windows.Point(0.5, 1)),
                        };
                        Background = new System.Windows.Media.LinearGradientBrush(a, b, sp, ep);
                    }
                    break;
                }
                case "Solid":
                {
                    if (ParseColor(_manifest.BackgroundColor1) is { } c)
                        Background = new System.Windows.Media.SolidColorBrush(c);
                    break;
                }
            }

            if (_manifest.FixedSize)
                ResizeMode = ResizeMode.NoResize;
        }
        catch { }
    }

    private static System.Windows.Media.Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;
        try { return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex); }
        catch { return null; }
    }

    // ── Maintenance: Repair / Remove when the bundle is already installed ──────

    // An app counts as installed if its Control Panel entry exists; its InstallLocation gives the dir.
    private List<(InstallApp App, string Dir)> GetInstalledApps()
    {
        var result = new List<(InstallApp, string)>();
        if (_manifest is null)
            return result;

        foreach (var app in _manifest.Apps)
        {
            var keyName = Sanitize($"{_manifest.SetupName}_{app.Name}");
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{keyName}");
                if (key?.GetValue("InstallLocation") is string loc
                    && !string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc))
                {
                    result.Add((app, loc));
                }
            }
            catch { }
        }
        return result;
    }

    private void ShowMaintenancePage(List<(InstallApp App, string Dir)> installed)
    {
        PageEula.Visibility = Visibility.Collapsed;
        PagePath.Visibility = Visibility.Collapsed;
        PagePrereq.Visibility = Visibility.Collapsed;
        PageMaintenance.Visibility = Visibility.Visible;
        ActionBtn.Visibility = Visibility.Collapsed;
        BackBtn.Visibility = Visibility.Collapsed;
        FinishBtn.Visibility = Visibility.Collapsed;

        MaintenanceInfoText.Text =
            $"{_manifest!.SetupName} is already installed. Choose which application(s) to repair (reinstall) or remove.";

        _maintItems.Clear();
        MaintenanceAppList.Children.Clear();
        foreach (var (app, dir) in installed)
        {
            var cb = new System.Windows.Controls.CheckBox
            {
                Content = app.Name,
                IsChecked = true,
                FontSize = 13,
                Margin = new Thickness(0, 4, 0, 4),
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
            };
            MaintenanceAppList.Children.Add(cb);
            _maintItems.Add((cb, app, dir));
        }
    }

    private async void RepairBtn_Click(object sender, RoutedEventArgs e)
    {
        var sel = _maintItems.Where(i => i.Check.IsChecked == true).ToList();
        if (sel.Count == 0)
            return;

        var progress = StartMaintenanceProgress("Repairing…");
        await Task.Run(() =>
        {
            for (var i = 0; i < sel.Count; i++)
            {
                progress.Report((100.0 * i / sel.Count, $"Repairing {sel[i].App.Name}…"));
                RepairApp(sel[i].App, sel[i].Dir);
            }
            progress.Report((100, "Done."));
        });
        ShowMaintenanceDone("✔ Repair Complete", "The selected application(s) have been reinstalled.");
    }

    private async void RemoveBtn_Click(object sender, RoutedEventArgs e)
    {
        var sel = _maintItems.Where(i => i.Check.IsChecked == true).ToList();
        if (sel.Count == 0)
            return;

        var progress = StartMaintenanceProgress("Removing…");
        await Task.Run(() =>
        {
            for (var i = 0; i < sel.Count; i++)
            {
                progress.Report((100.0 * i / sel.Count, $"Removing {sel[i].App.Name}…"));
                RemoveApp(sel[i].App, sel[i].Dir);
            }
            progress.Report((100, "Done."));
        });
        ShowMaintenanceDone("✔ Removal Complete", "The selected application(s) have been removed.");
    }

    private IProgress<(double Pct, string Status)> StartMaintenanceProgress(string status)
    {
        PageMaintenance.Visibility = Visibility.Collapsed;
        PageProgress.Visibility = Visibility.Visible;
        CancelBtn.IsEnabled = false;
        ProgressStatus.Text = status;
        ProgressFileText.Text = string.Empty;
        InstallProgress.Value = 0;
        return new Progress<(double Pct, string Status)>(p =>
        {
            InstallProgress.Value = p.Pct;
            ProgressStatus.Text = p.Status;
        });
    }

    // Re-copies an app's files from the embedded package over its install dir.
    private void RepairApp(InstallApp app, string dir)
    {
        var src = Path.Combine(_tempDir, "apps", app.DefaultInstallDir);
        if (!Directory.Exists(src))
            return;

        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dir, rel);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
            catch { }
        }
    }

    // Removes an app via its install log (registry), then deletes its whole folder. The setup
    // process isn't running from the app folder, so it can be deleted outright.
    private static void RemoveApp(InstallApp app, string dir)
    {
        var logPath = Path.Combine(dir, "install-log.json");
        if (File.Exists(logPath))
        {
            try
            {
                var log = JsonSerializer.Deserialize<InstallLog>(File.ReadAllText(logPath));
                if (log is not null && !string.IsNullOrWhiteSpace(log.UninstallKeyName))
                {
                    foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
                    {
                        try
                        {
                            using var k = root.OpenSubKey(
                                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
                            k?.DeleteSubKeyTree(log.UninstallKeyName, throwOnMissingSubKey: false);
                        }
                        catch { }
                    }
                }

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
            }
            catch { }
        }

        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { }
    }

    private void ShowMaintenanceDone(string header, string message)
    {
        PageProgress.Visibility = Visibility.Collapsed;
        PageComplete.Visibility = Visibility.Visible;
        CompleteHeaderText.Text = header;
        CompleteText.Text = message;
        LaunchCheckbox.Visibility = Visibility.Collapsed;
        CancelBtn.Visibility = Visibility.Collapsed;
        FinishBtn.Visibility = Visibility.Visible;
        _installed = true;
    }

    // Resolves the EXE to launch on the final page: the bundle's chosen launch app (LaunchAppDir
    // + LaunchExeName), else the first app. The exe is searched recursively so it's found even
    // when it lives in a subfolder of the app's install directory.
    private string? ResolveLaunchExe(string installDir)
    {
        if (_manifest is null)
            return null;

        if (!string.IsNullOrWhiteSpace(_manifest.LaunchAppDir))
            return FindExeInDir(Path.Combine(installDir, _manifest.LaunchAppDir), _manifest.LaunchExeName);

        var firstApp = _manifest.Apps.FirstOrDefault();
        if (firstApp is null)
            return null;

        return FindExeInDir(Path.Combine(installDir, firstApp.DefaultInstallDir), firstApp.LaunchExeName);
    }

    // Locates a specific exe by name within an app folder (recursive), with no fallback.
    private static string? FindNamedExe(string dir, string exeName)
    {
        if (!Directory.Exists(dir) || string.IsNullOrWhiteSpace(exeName))
            return null;
        var direct = Path.Combine(dir, exeName);
        if (File.Exists(direct))
            return direct;
        try { return Directory.EnumerateFiles(dir, exeName, SearchOption.AllDirectories).FirstOrDefault(); }
        catch { return null; }
    }

    private static string? FindExeInDir(string dir, string? exeName)
    {
        if (!Directory.Exists(dir))
            return null;

        if (!string.IsNullOrWhiteSpace(exeName))
        {
            var direct = Path.Combine(dir, exeName);
            if (File.Exists(direct))
                return direct;
            try
            {
                var hit = Directory.EnumerateFiles(dir, exeName, SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null)
                    return hit;
            }
            catch { }
        }

        // Fallback: any exe — top level first, then anywhere under the app folder.
        var top = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (top is not null)
            return top;
        try { return Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories).FirstOrDefault(); }
        catch { return null; }
    }

    private void ShowUninstallPage()
    {
        PageEula.Visibility = Visibility.Collapsed;
        PagePath.Visibility = Visibility.Collapsed;
        PagePrereq.Visibility = Visibility.Collapsed;
        PageRedist.Visibility = Visibility.Collapsed;
        PageProgress.Visibility = Visibility.Collapsed;
        PageComplete.Visibility = Visibility.Collapsed;

        PageUninstall.Visibility = Visibility.Visible;

        CancelBtn.Visibility = Visibility.Collapsed;
        ActionBtn.Visibility = Visibility.Collapsed;
        FinishBtn.Visibility = Visibility.Collapsed;

        UninstallInfoText.Text = "This file is the uninstaller component.\n\n" +
            "Click Uninstall to remove the application and all its files from this computer.";

        // Set window icon to a standard uninstall icon from imageres.dll
        try
        {
            // ExtractIcon only needs a non-null module handle; GetHINSTANCE returning -1 in a
            // single-file build is harmless here (the icon path is what matters), so suppress IL3002.
#pragma warning disable IL3002
            var hModule = System.Runtime.InteropServices.Marshal.GetHINSTANCE(GetType().Module);
#pragma warning restore IL3002
            var hIcon = NativeMethods.ExtractIcon(
                hModule,
                Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\imageres.dll"), -131);
            if (hIcon != IntPtr.Zero)
            {
                Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                NativeMethods.DestroyIcon(hIcon);
            }
        }
        catch { }
    }

    private async void UninstallBtn_Click(object sender, RoutedEventArgs e)
    {
        UninstallBtn.IsEnabled = false;
        PageUninstall.Visibility = Visibility.Collapsed;
        PageUninstallProgress.Visibility = Visibility.Visible;
        UninstallStatusText.Text = "Uninstalling…";

        await Task.Run(() => App.PerformUninstall(showMessages: false));

        PageUninstallProgress.Visibility = Visibility.Collapsed;
        PageUninstallComplete.Visibility = Visibility.Visible;
        UninstallCompleteText.Text = "The application has been uninstalled.";
        UninstallFinishBtn.Visibility = Visibility.Visible;
    }

    private void UninstallFinishBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowError(string message)
    {
        PageEula.Visibility = Visibility.Collapsed;
        PagePath.Visibility = Visibility.Collapsed;
        PagePrereq.Visibility = Visibility.Collapsed;
        PageRedist.Visibility = Visibility.Collapsed;
        PageProgress.Visibility = Visibility.Collapsed;
        PageComplete.Visibility = Visibility.Visible;
        CancelBtn.Visibility = Visibility.Collapsed;
        ActionBtn.Visibility = Visibility.Collapsed;
        FinishBtn.Visibility = Visibility.Visible;

        TitleText.Text = "Installation Failed";
        TitleText.Foreground = System.Windows.Media.Brushes.IndianRed;
        CompleteText.Text = message;
        LaunchCheckbox.Visibility = Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => Close();

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (LaunchCheckbox.IsChecked == true)
        {
            var exe = ResolveLaunchExe(InstallPathBox.Text.Trim());
            if (exe is not null)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = exe, UseShellExecute = true }); }
                catch { }
            }
        }
        Close();
    }

    // Locates the appended payload footer ([8-byte ZIP offset][7-byte "STUPEND" magic]).
    // Returns the ZIP start offset and the ZIP end (= the absolute position where the footer
    // begins). The footer is NOT necessarily at end-of-file: when the setup is Authenticode
    // signed, signtool appends the certificate (plus up to 8 bytes of alignment padding) AFTER
    // the footer, so we must scan backward for the magic and bound the ZIP by the footer
    // position — never by fs.Length.
    private static (long ZipOffset, long ZipEnd) FindZipFooter(string exePath)
    {
        using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        if (fs.Length < EndMagic.Length + 8)
            throw new InvalidDataException("File too small to contain package data.");

        var searchWindow = (int)Math.Min(65536, fs.Length);
        var buffer = new byte[searchWindow];
        var windowStart = fs.Length - searchWindow;
        fs.Seek(windowStart, SeekOrigin.Begin);
        fs.ReadExactly(buffer);

        for (var i = searchWindow - EndMagic.Length; i >= 8; i--)
        {
            if (buffer.AsSpan(i, EndMagic.Length).SequenceEqual(EndMagic))
            {
                var zipOffset = BitConverter.ToInt64(buffer, i - 8);
                var footerStart = windowStart + i - 8; // true end of the ZIP payload
                if (zipOffset < 0 || zipOffset > footerStart)
                    throw new InvalidDataException("Invalid ZIP offset in footer.");
                return (zipOffset, footerStart);
            }
        }

        throw new InvalidDataException("No setup package found in this file.");
    }

    private static void ExtractZipFromSelf(string exePath, string targetDir)
    {
        var (zipOffset, zipEnd) = FindZipFooter(exePath);

        using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var zipLength = zipEnd - zipOffset;

        fs.Seek(zipOffset, SeekOrigin.Begin);

        using var zipStream = new MemoryStream((int)zipLength);
        var buffer = new byte[65536];
        long remaining = zipLength;
        while (remaining > 0)
        {
            var read = fs.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
            if (read == 0) break;
            zipStream.Write(buffer, 0, read);
            remaining -= read;
        }

        zipStream.Position = 0;
        ZipFile.ExtractToDirectory(zipStream, targetDir, overwriteFiles: true);
    }

    // Resolves an installed file to use as an app's Control Panel DisplayIcon: the user-chosen
    // icon first, then the launch EXE. Tries the relative path, then a recursive name match.
    private static string? ResolveInstalledIcon(string appDir, InstallApp app)
    {
        foreach (var name in new[] { app.IconFileName, app.LaunchExeName })
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var direct = Path.Combine(appDir, name);
            if (File.Exists(direct))
                return direct;

            try
            {
                var hit = Directory.EnumerateFiles(appDir, Path.GetFileName(name), SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (hit is not null)
                    return hit;
            }
            catch { }
        }

        return null;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }
}

internal sealed class InstallAppItem
{
    public string Name { get; set; } = string.Empty;
    public string SubPath { get; set; } = string.Empty;
    public bool CreateShortcut { get; set; } = true;
}

internal sealed class RedistDetectionResult
{
    public string Name { get; set; } = string.Empty;
    public bool IsDetected { get; set; }
    public string StatusIcon => IsDetected ? "✔" : "✗";
    public string StatusText => IsDetected ? "Detected" : "Not detected";
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
