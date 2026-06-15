using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class SetupViewModel : ObservableObject
{
    private readonly ISetupStorageService _setupStorage;
    private readonly IStorageService _storage;
    private readonly ISetupService _setupService;
    private readonly IDialogService _dialog;
    private readonly ILogService _log;
    private readonly ISettingsService _settings;
    private MainViewModel _main = null!;
    private SetupBundle? _editingBundle;

    private const string DefaultEulaText =
"""
END USER LICENSE AGREEMENT

IMPORTANT – READ CAREFULLY: This End User License Agreement ("EULA") is a legal agreement between you and the publisher of this software. By installing, copying, or otherwise using the software, you agree to be bound by the terms of this EULA.

This software is provided "as is" without warranty of any kind, express or implied, including but not limited to the warranties of merchantability, fitness for a particular purpose, and noninfringement. In no event shall the authors or copyright holders be liable for any claim, damages, or other liability arising from the use of the software.

SOFTWARE LICENSE

1. Grant of License. This EULA grants you a non-exclusive, non-transferable license to install and use the software for your personal or business use.

2. Restrictions. You may not modify, reverse engineer, decompile, or disassemble the software, except to the extent that such activity is expressly permitted by applicable law.

3. Termination. This license is effective until terminated. Your rights under this license will terminate automatically without notice if you fail to comply with any terms of this EULA.
""";

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private double _generationPercent;
    [ObservableProperty] private string _generationMessage = string.Empty;
    [ObservableProperty] private bool _showList = true;

    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editVersion = string.Empty;
    [ObservableProperty] private string _editOutputFolder = string.Empty;
    [ObservableProperty] private bool _editSignOutput;
    [ObservableProperty] private string _editEulaText = string.Empty;
    [ObservableProperty] private string? _editBannerImage;
    [ObservableProperty] private string? _editSetupIcon;
    [ObservableProperty] private SetupBundleAppItem? _editLaunchApp;
    [ObservableProperty] private string? _editLaunchExe;

    // When the launch app changes, default its exe to the app's first exe.
    partial void OnEditLaunchAppChanged(SetupBundleAppItem? value)
        => EditLaunchExe = value?.AppExes.FirstOrDefault();

    // ── Setup appearance ──────────────────────────────────────────────────────
    [ObservableProperty] private string _editBackgroundMode = "Default";
    [ObservableProperty] private string _editBgColor1 = "#1C1C1E";
    [ObservableProperty] private string _editBgColor2 = "#3A3A3C";
    [ObservableProperty] private string _editBgGradientDir = "Vertical";
    [ObservableProperty] private string? _editBackgroundImage;
    [ObservableProperty] private bool _editFixedSize;

    public string[] BackgroundModes { get; } = ["Default", "Solid", "Gradient", "Image"];
    public string[] GradientDirections { get; } = ["Vertical", "Horizontal", "Diagonal"];

    public string SigningConfigInfo
    {
        get
        {
            var g = _settings.Global;
            if (g.UseStoreCert && !string.IsNullOrWhiteSpace(g.StoreCertThumbprint))
                return $"Using store certificate (thumbprint: {g.StoreCertThumbprint[..8]}…)";
            if (!string.IsNullOrWhiteSpace(g.GlobalCertPath))
                return $"Using PFX: {g.GlobalCertPath}";
            return "No signing certificate configured — go to Settings > Global Options";
        }
    }

    public ObservableCollection<SetupBundle> Bundles { get; } = [];
    public ObservableCollection<SetupBundleAppItem> AvailableApps { get; } = [];
    public ObservableCollection<RedistItem> WorkingRedists { get; } = [];

    public SetupViewModel(ISetupStorageService setupStorage, IStorageService storage,
        ISetupService setupService, IDialogService dialog, ILogService log,
        ISettingsService settings)
    {
        _setupStorage = setupStorage;
        _storage = storage;
        _setupService = setupService;
        _dialog = dialog;
        _log = log;
        _settings = settings;
        Reload();
    }

    public void Initialize(MainViewModel main)
    {
        _main = main;
        Reload();
    }

    private void Reload()
    {
        Bundles.Clear();
        foreach (var b in _setupStorage.GetAll())
            Bundles.Add(b);
    }

    [RelayCommand]
    private void AddRegistryEntry(SetupBundleAppItem? appItem)
    {
        if (appItem is null) return;
        var company = _settings.Global.CompanyName;
        var keyPath = string.IsNullOrWhiteSpace(company)
            ? $"SOFTWARE\\{appItem.AppName}"
            : $"SOFTWARE\\{company}\\{appItem.AppName}";
        // Default the exe to the chosen launch exe, else the app's first exe, else <AppName>.exe.
        // (LaunchExeName carries the "(Let Windows decide)" placeholder when unset.)
        var exeName = appItem.LaunchExeName switch
        {
            null or "" or "(Let Windows decide)" => appItem.AppExes.FirstOrDefault() ?? appItem.AppName + ".exe",
            _ => appItem.LaunchExeName,
        };
        appItem.RegistryEntries.Add(new RegistryItem
        {
            Root = "HKCU",
            KeyPath = keyPath,
            ValueName = "ExecutablePath",
            ExeName = exeName, // sets ValueData to [InstallDir]\<exe> via its change handler
            ValueKind = "String"
        });
    }

    [RelayCommand]
    private void RemoveRegistryEntry(RegistryItem? item)
    {
        if (item is null) return;
        foreach (var appItem in AvailableApps)
            appItem.RegistryEntries.Remove(item);
    }

    [RelayCommand]
    private void CloseSetups()
    {
        if (_main.SelectedApp is not null)
            _main.NavigateToDetail(_main.SelectedApp.Entry);
        else
            _main.NavigateToWelcome();
    }

    [RelayCommand]
    private void GoToList()
    {
        IsEditing = false;
        ShowList = true;
        Reload();
    }

    [RelayCommand]
    private void NewSetup()
    {
        _editingBundle = null;
        EditName = string.Empty;
        EditVersion = string.Empty;
        EditOutputFolder = string.Empty;
        EditSignOutput = false;
        EditEulaText = DefaultEulaText;
        EditBannerImage = null;
        EditSetupIcon = null;
        EditLaunchApp = null;
        EditBackgroundMode = "Default";
        EditBgColor1 = "#1C1C1E";
        EditBgColor2 = "#3A3A3C";
        EditBgGradientDir = "Vertical";
        EditBackgroundImage = null;
        EditFixedSize = false;
        AvailableApps.Clear();
        WorkingRedists.Clear();

        foreach (var app in _storage.GetAll())
            AvailableApps.Add(MakeAppItem(app, null));

        IsEditing = true;
        ShowList = false;
    }

    private SetupBundleAppItem MakeAppItem(AppEntry app, SetupAppRef? existing)
    {
        var item = new SetupBundleAppItem
        {
            AppId = app.Id,
            AppName = app.Name,
            IsSelected = existing is not null,
            VersionMode = existing?.VersionMode ?? VersionMode.Cumulative,
            LaunchExeName = existing?.LaunchExeName,
            SetupIcon = existing?.SetupIconPath,
            CreateShortcut = existing?.CreateShortcut ?? true,
        };

        if (existing is not null)
        {
            foreach (var r in existing.RegistryEntries)
                item.RegistryEntries.Add(new RegistryItem
                {
                    Root = r.Root,
                    KeyPath = r.KeyPath,
                    ValueName = r.ValueName,
                    ValueData = r.ValueData,
                    ValueKind = r.ValueKind,
                });
        }

        // Placeholder + all .exe files from the app's versions
        const string placeholder = "(Let Windows decide)";
        item.AvailableExes.Add(placeholder);

        var exes = app.Versions
            .Where(v => v.Status != VersionStatus.Retracted && v.Status != VersionStatus.Scrapped)
            .SelectMany(v => v.Files)
            .Where(f => !f.IsDebug && ".exe".Equals(Path.GetExtension(f.Path), StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileName(f.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();

        foreach (var exe in exes)
        {
            item.AvailableExes.Add(exe);
            item.AppExes.Add(exe);
            item.AdminExes.Add(new AdminExeItem
            {
                Name = exe,
                IsAdmin = existing?.RunAsAdminExes.Contains(exe, StringComparer.OrdinalIgnoreCase) ?? false,
            });
        }

        // Populate AvailableIcons: placeholder + exe files + .ico files from versions
        const string iconPlaceholder = "(From launch exe)";
        item.AvailableIcons.Add(iconPlaceholder);

        var iconFiles = app.Versions
            .Where(v => v.Status != VersionStatus.Retracted && v.Status != VersionStatus.Scrapped)
            .SelectMany(v => v.Files)
            .Where(f => !f.IsDebug && (".exe".Equals(Path.GetExtension(f.Path), StringComparison.OrdinalIgnoreCase) ||
                                       ".ico".Equals(Path.GetExtension(f.Path), StringComparison.OrdinalIgnoreCase)))
            .Select(f => Path.GetFileName(f.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();

        foreach (var ico in iconFiles)
            item.AvailableIcons.Add(ico);

        // Default selection
        if (existing?.LaunchExeName is null)
            item.LaunchExeName = placeholder;

        if (string.IsNullOrWhiteSpace(existing?.SetupIconPath))
            item.SetupIcon = iconPlaceholder;

        return item;
    }

    [RelayCommand]
    private void EditSetup(SetupBundle? bundle)
    {
        if (bundle is null) return;
        _editingBundle = bundle;
        EditName = bundle.Name;
        EditVersion = bundle.Version;
        EditOutputFolder = bundle.OutputFolder;
        EditSignOutput = bundle.SignOutput;
        EditEulaText = string.IsNullOrWhiteSpace(bundle.EulaText) ? DefaultEulaText : bundle.EulaText;
        EditBannerImage = bundle.BannerImage;
        EditSetupIcon = bundle.SetupIconPath;
        EditBackgroundMode = string.IsNullOrWhiteSpace(bundle.BackgroundMode) ? "Default" : bundle.BackgroundMode;
        EditBgColor1 = string.IsNullOrWhiteSpace(bundle.BackgroundColor1) ? "#1C1C1E" : bundle.BackgroundColor1;
        EditBgColor2 = string.IsNullOrWhiteSpace(bundle.BackgroundColor2) ? "#3A3A3C" : bundle.BackgroundColor2;
        EditBgGradientDir = string.IsNullOrWhiteSpace(bundle.BackgroundGradientDirection) ? "Vertical" : bundle.BackgroundGradientDirection;
        EditBackgroundImage = bundle.BackgroundImage;
        EditFixedSize = bundle.FixedSize;
        AvailableApps.Clear();
        WorkingRedists.Clear();

        foreach (var app in _storage.GetAll())
        {
            var existing = bundle.Apps.FirstOrDefault(a => a.AppId == app.Id);
            AvailableApps.Add(MakeAppItem(app, existing));
        }

        // Resolve the launch app to the actual item instance (after AvailableApps is populated).
        EditLaunchApp = AvailableApps.FirstOrDefault(a => a.AppId == bundle.LaunchAppId);
        // Setting EditLaunchApp defaulted the exe; restore the saved one if present.
        if (EditLaunchApp is not null && !string.IsNullOrWhiteSpace(bundle.LaunchExeName))
            EditLaunchExe = bundle.LaunchExeName;

        foreach (var r in bundle.Redists)
            WorkingRedists.Add(new RedistItem
            {
                Name = r.Name,
                SourcePath = r.SourcePath,
                Arguments = r.Arguments,
                DetectionKeyPath = r.DetectionKeyPath,
                DetectionValueName = r.DetectionValueName,
                DetectionExpectedValue = r.DetectionExpectedValue,
            });

        IsEditing = true;
        ShowList = false;
    }

    [RelayCommand]
    private void BrowseOutputFolder()
    {
        var folder = _dialog.OpenFolder("Select output folder for the setup executable");
        if (folder is not null)
            EditOutputFolder = folder;
    }

    [RelayCommand]
    private void BrowseBannerImage()
    {
        var path = _dialog.OpenFile("Select banner image",
            "Image files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*");
        if (path is not null)
            EditBannerImage = path;
    }

    [RelayCommand]
    private void ClearBannerImage()
    {
        EditBannerImage = null;
    }

    [RelayCommand]
    private void BrowseBundleSetupIcon()
    {
        var path = _dialog.OpenFile("Select setup icon",
            "Icon or executable (*.ico;*.exe)|*.ico;*.exe|Icon files (*.ico)|*.ico|Executable (*.exe)|*.exe|All files (*.*)|*.*");
        if (path is not null)
            EditSetupIcon = path;
    }

    [RelayCommand]
    private void ClearBundleSetupIcon()
    {
        EditSetupIcon = null;
    }

    [RelayCommand]
    private void BrowseBackgroundImage()
    {
        var path = _dialog.OpenFile("Select background image",
            "Image files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*");
        if (path is not null)
        {
            EditBackgroundImage = path;
            EditBackgroundMode = "Image";
        }
    }

    [RelayCommand]
    private void ClearBackgroundImage()
    {
        EditBackgroundImage = null;
    }

    [RelayCommand]
    private void ClearLaunchApp()
    {
        EditLaunchApp = null;
    }

    [RelayCommand]
    private void BrowseSetupIcon(SetupBundleAppItem? appItem)
    {
        if (appItem is null) return;
        var path = _dialog.OpenFile("Select setup icon",
            "Icon files (*.ico)|*.ico|Executable (*.exe)|*.exe|All files (*.*)|*.*");
        if (path is not null)
        {
            var name = Path.GetFileName(path);
            if (!appItem.AvailableIcons.Contains(name))
                appItem.AvailableIcons.Add(name);
            appItem.SetupIcon = name;
        }
    }

    [RelayCommand]
    private void ResetEulaText()
    {
        EditEulaText = DefaultEulaText;
    }

    [RelayCommand]
    private void AddRedist()
    {
        WorkingRedists.Add(new RedistItem());
    }

    [RelayCommand]
    private void RemoveRedist(RedistItem? item)
    {
        if (item is not null)
            WorkingRedists.Remove(item);
    }

    [RelayCommand]
    private void BrowseRedistExe(RedistItem? item)
    {
        if (item is null) return;
        var path = _dialog.OpenFile("Select redistributable executable",
            "Executable (*.exe)|*.exe|All files (*.*)|*.*");
        if (path is not null)
        {
            item.SourcePath = path;
            if (string.IsNullOrWhiteSpace(item.Name))
                item.Name = Path.GetFileNameWithoutExtension(path);
        }
    }

    [RelayCommand]
    private void SetRedistPreset(string? preset)
    {
        if (preset is null) return;
        var entry = preset switch
        {
            "vc2015-2022" => new RedistItem
            {
                Name = "VC++ 2015-2022 Redist (x64)",
                Arguments = "/install /quiet /norestart",
                DetectionKeyPath = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64",
                DetectionValueName = "Installed",
                DetectionExpectedValue = "1",
                SourcePath = string.Empty,
            },
            "vc2013" => new RedistItem
            {
                Name = "VC++ 2013 Redist (x64)",
                Arguments = "/install /quiet /norestart",
                DetectionKeyPath = @"SOFTWARE\Microsoft\VisualStudio\12.0\VC\Runtimes\x64",
                DetectionValueName = "Installed",
                DetectionExpectedValue = "1",
                SourcePath = string.Empty,
            },
            "dotnet6" => new RedistItem
            {
                Name = ".NET Desktop Runtime 6.0 (x64)",
                Arguments = "/install /quiet /norestart",
                DetectionKeyPath = @"FILE:Microsoft.WindowsDesktop.App",
                DetectionValueName = "6.",
                DetectionExpectedValue = string.Empty,
                SourcePath = string.Empty,
            },
            "dotnet8" => new RedistItem
            {
                Name = ".NET Desktop Runtime 8.0 (x64)",
                Arguments = "/install /quiet /norestart",
                DetectionKeyPath = @"FILE:Microsoft.WindowsDesktop.App",
                DetectionValueName = "8.",
                DetectionExpectedValue = string.Empty,
                SourcePath = string.Empty,
            },
            "dotnet10" => new RedistItem
            {
                Name = ".NET Desktop Runtime 10.0 (x64)",
                Arguments = "/install /quiet /norestart",
                DetectionKeyPath = @"FILE:Microsoft.WindowsDesktop.App",
                DetectionValueName = "10.",
                DetectionExpectedValue = string.Empty,
                SourcePath = string.Empty,
            },
            _ => null,
        };
        if (entry is not null)
            WorkingRedists.Add(entry);
    }

    [RelayCommand]
    private void SaveSetup()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            _dialog.Alert("Validation", "Setup name is required.");
            return;
        }
        if (string.IsNullOrWhiteSpace(EditOutputFolder))
        {
            _dialog.Alert("Validation", "Output folder is required.");
            return;
        }

        var selectedApps = AvailableApps
            .Where(a => a.IsSelected)
            .Select(a => new SetupAppRef
            {
                AppId = a.AppId,
                VersionMode = a.VersionMode,
                LaunchExeName = a.LaunchExeName switch
                {
                    null or "" or "(Let Windows decide)" => null,
                    _ => a.LaunchExeName,
                },
                SetupIconPath = a.SetupIcon switch
                {
                    null or "" or "(From launch exe)" => null,
                    _ => a.SetupIcon,
                },
                CreateShortcut = a.CreateShortcut,
                RunAsAdminExes = a.AdminExes.Where(x => x.IsAdmin).Select(x => x.Name).ToList(),
                RegistryEntries = a.RegistryEntries
                    .Where(r => !string.IsNullOrWhiteSpace(r.KeyPath))
                    .Select(r => new RegistryEntry
                    {
                        Root = r.Root,
                        KeyPath = r.KeyPath,
                        ValueName = r.ValueName,
                        ValueData = r.ValueData,
                        ValueKind = r.ValueKind,
                    })
                    .ToList(),
            })
            .ToList();

        if (selectedApps.Count == 0)
        {
            _dialog.Alert("Validation", "Select at least one app.");
            return;
        }

        // If a launch-on-finish app is chosen, an exe must be selected for it.
        if (EditLaunchApp is not null && selectedApps.Any(a => a.AppId == EditLaunchApp.AppId)
            && string.IsNullOrWhiteSpace(EditLaunchExe))
        {
            _dialog.Alert("Validation", $"Select the exe to launch for '{EditLaunchApp.AppName}', or set Launch on Finish to None.");
            return;
        }

        var bundle = _editingBundle ?? new SetupBundle();
        bundle.Name = EditName;
        bundle.Version = EditVersion;
        bundle.OutputFolder = EditOutputFolder;
        bundle.SignOutput = EditSignOutput;
        bundle.EulaText = EditEulaText;
        bundle.BannerImage = string.IsNullOrWhiteSpace(EditBannerImage) ? null : EditBannerImage;
        bundle.SetupIconPath = string.IsNullOrWhiteSpace(EditSetupIcon) ? null : EditSetupIcon;
        bundle.BackgroundMode = EditBackgroundMode == "Default" ? null : EditBackgroundMode;
        bundle.BackgroundColor1 = EditBgColor1;
        bundle.BackgroundColor2 = EditBgColor2;
        bundle.BackgroundGradientDirection = EditBgGradientDir;
        bundle.BackgroundImage = string.IsNullOrWhiteSpace(EditBackgroundImage) ? null : EditBackgroundImage;
        bundle.FixedSize = EditFixedSize;
        // Only honor the launch app if it's actually one of the selected apps.
        var launchValid = EditLaunchApp is not null && selectedApps.Any(a => a.AppId == EditLaunchApp.AppId);
        bundle.LaunchAppId = launchValid ? EditLaunchApp!.AppId : null;
        bundle.LaunchExeName = launchValid ? EditLaunchExe : null;
        bundle.Apps = selectedApps;
        bundle.Redists = WorkingRedists
            .Where(r => !string.IsNullOrWhiteSpace(r.SourcePath))
            .Select(r => new RedistEntry
            {
                Name = r.Name,
                SourcePath = r.SourcePath,
                Arguments = r.Arguments,
                DetectionKeyPath = r.DetectionKeyPath,
                DetectionValueName = r.DetectionValueName,
                DetectionExpectedValue = r.DetectionExpectedValue,
            })
            .ToList();

        _setupStorage.Save(bundle);
        _log.Write("Setups", $"Saved setup bundle: {bundle.Name} ({bundle.Id})");
        GoToList();
    }

    [RelayCommand]
    private void DeleteSetup(SetupBundle? bundle)
    {
        if (bundle is null) return;
        if (!_dialog.Confirm("Delete Setup",
                $"Delete setup bundle '{bundle.Name}'? This cannot be undone.",
                "Delete")) return;

        _setupStorage.Delete(bundle.Id);
        _log.Write("Setups", $"Deleted setup bundle: {bundle.Name}");
        Reload();
    }

    [RelayCommand]
    private async Task GenerateSetup(SetupBundle? bundle)
    {
        if (bundle is null) return;

        IsGenerating = true;
        GenerationPercent = 0;
        GenerationMessage = "Starting…";
        try
        {
            var progress = new Progress<SetupProgressInfo>(info =>
            {
                GenerationPercent = info.Percent;
                GenerationMessage = info.Message;
                _log.Write("SetupGen", info.Message);
            });

            var setupPath = await Task.Run(() =>
                _setupService.GenerateAsync(bundle, progress));

            bundle.LastGeneratedPath = setupPath;
            bundle.LastGeneratedDate = DateTime.Now;
            _setupStorage.Save(bundle);
            Reload();

            _log.Write("Setups", $"Setup generated: {setupPath}");
            _dialog.Alert("Setup Generated",
                $"Setup saved to:\n{setupPath}");
        }
        catch (Exception ex)
        {
            _log.Write("SetupGen", $"Generation failed: {ex.Message}");
            _dialog.Alert("Generation Failed", ex.Message);
        }
        finally
        {
            IsGenerating = false;
        }
    }
}

public partial class SetupBundleAppItem : ObservableObject
{
    [ObservableProperty] private string _appId = string.Empty;
    [ObservableProperty] private string _appName = string.Empty;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private VersionMode _versionMode = VersionMode.Cumulative;
    [ObservableProperty] private string? _launchExeName;
    [ObservableProperty] private string? _setupIcon;
    [ObservableProperty] private bool _createShortcut = true;

    public ObservableCollection<string> AvailableExes { get; } = [];
    public ObservableCollection<string> AppExes { get; } = []; // exe files only (no placeholder), for registry pickers
    public ObservableCollection<string> AvailableIcons { get; } = [];
    public ObservableCollection<AdminExeItem> AdminExes { get; } = []; // per-exe "run as admin" toggles
    public ObservableCollection<RegistryItem> RegistryEntries { get; } = [];

    public int VersionModeIndex
    {
        get => (int)VersionMode;
        set => VersionMode = (VersionMode)value;
    }
}

public partial class AdminExeItem : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    [ObservableProperty] private bool _isAdmin;
}

public partial class RedistItem : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _sourcePath = string.Empty;
    [ObservableProperty] private string _arguments = "/install /quiet /norestart";
    [ObservableProperty] private string _detectionKeyPath = string.Empty;
    [ObservableProperty] private string _detectionValueName = string.Empty;
    [ObservableProperty] private string _detectionExpectedValue = string.Empty;
}

public partial class RegistryItem : ObservableObject
{
    [ObservableProperty] private string _root = "HKCU";
    [ObservableProperty] private string _keyPath = string.Empty;
    [ObservableProperty] private string _valueName = string.Empty;
    [ObservableProperty] private string _valueData = string.Empty;
    [ObservableProperty] private string _valueKind = "String";

    // Convenience exe picker: choosing an exe fills ValueData with [InstallDir]\<exe>.
    [ObservableProperty] private string? _exeName;

    partial void OnExeNameChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            ValueData = $"[InstallDir]\\{value}";
    }
}
