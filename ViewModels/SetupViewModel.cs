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

    // Generation overlay stays up after the run to show the result (success/failure) in-place,
    // instead of a separate dialog. IsGenerating = running phase; result phase shows when it clears.
    [ObservableProperty] private bool _showGenerationOverlay;
    [ObservableProperty] private bool _generationSucceeded;
    [ObservableProperty] private string _generationResultDetail = string.Empty;
    private string? _lastGeneratedSetupPath;

    // ── Sidebar categories (Setup Bundles / Past Bundles) ─────────────────
    public string[] Categories { get; } = { "Setup Bundles", "Past Bundles" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBundlesCategory))]
    [NotifyPropertyChangedFor(nameof(IsPastCategory))]
    private string _selectedCategory = "Setup Bundles";

    public bool IsBundlesCategory => SelectedCategory == "Setup Bundles";
    public bool IsPastCategory    => SelectedCategory == "Past Bundles";

    /// <summary>Generation history, newest first ("Past Bundles").</summary>
    public ObservableCollection<GeneratedSetupRecord> History { get; } = [];

    private bool _suppressCategoryChange;

    partial void OnSelectedCategoryChanged(string? oldValue, string newValue)
    {
        if (_suppressCategoryChange) return;

        // Navigating categories leaves the wizard — confirm if mid-edit so edits aren't lost silently.
        if (IsEditing)
        {
            if (!_dialog.Confirm("Discard Changes",
                    "Leave the setup editor? Unsaved changes to this bundle will be lost.",
                    "Discard"))
            {
                _suppressCategoryChange = true;
                SelectedCategory = oldValue ?? "Setup Bundles";
                _suppressCategoryChange = false;
                return;
            }
            IsEditing = false;
            ShowList = true;
        }

        if (IsPastCategory) ReloadHistory();
    }

    private void ReloadHistory()
    {
        History.Clear();
        foreach (var record in _setupStorage.GetHistory().OrderByDescending(h => h.GeneratedDate))
            History.Add(record);
    }

    [RelayCommand]
    private void ClearHistory()
    {
        if (!_dialog.Confirm("Clear History",
                "Remove all Past Bundles entries? Generated setup files on disk are not deleted.",
                "Clear")) return;
        _setupStorage.ClearHistory();
        ReloadHistory();
    }

    [RelayCommand]
    private void OpenSetupFolder(GeneratedSetupRecord? record)
    {
        if (record is not null) RevealInExplorer(record.OutputPath);
    }

    // Opens Explorer with the setup file selected (or its folder, if the file is gone).
    private void RevealInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (System.IO.File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else
            {
                var dir = System.IO.Path.GetDirectoryName(path);
                if (dir is not null && System.IO.Directory.Exists(dir))
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
                else
                    _dialog.Alert("Not Found", $"The setup file no longer exists:\n{path}");
            }
        }
        catch (Exception ex) { _dialog.Alert("Open Failed", ex.Message); }
    }

    // ── Generation pipeline stepper ───────────────────────────────────────
    // Maps the live GenerationPercent onto discrete phases so the setups list
    // shows the same "Done / Current / Pending" stepper as packaging.
    // Boundaries mirror the percent ranges reported by SetupService.GenerateAsync.
    private static readonly (int Start, string Label)[] GenSteps =
    {
        (0,  "Apps"),      // collect + stage app files       (5–40%)
        (40, "Redists"),   // runtimes + images               (40–56%)
        (56, "Manifest"),  // install manifest + uninstaller  (56–68%)
        (68, "Package"),   // ZIP payload                     (68–90%)
        (90, "Build"),     // bootstrapper + appended payload (90–96%)
        (96, "Sign"),      // sign the setup exe              (96–100%)
    };

    private int CurrentGenStep()
    {
        var p = GenerationPercent;
        var idx = 0;
        for (var i = 0; i < GenSteps.Length; i++)
            if (p >= GenSteps[i].Start) idx = i;
        return idx;
    }

    private string GenState(int index)
    {
        if (GenerationPercent >= 100) return "Done";
        var current = CurrentGenStep();
        return index < current ? "Done" : index == current ? "Current" : "Pending";
    }

    public string GenAppsState     => GenState(0);
    public string GenRedistsState  => GenState(1);
    public string GenManifestState => GenState(2);
    public string GenPackageState  => GenState(3);
    public string GenBuildState    => GenState(4);
    public string GenSignState     => GenState(5);

    /// <summary>Current step label, e.g. "Step 3 of 6 · Manifest".</summary>
    public string GenStageTitle =>
        GenerationPercent >= 100
            ? "Finishing…"
            : $"Step {CurrentGenStep() + 1} of {GenSteps.Length} · {GenSteps[CurrentGenStep()].Label}";

    partial void OnGenerationPercentChanged(double value)
    {
        OnPropertyChanged(nameof(GenAppsState));
        OnPropertyChanged(nameof(GenRedistsState));
        OnPropertyChanged(nameof(GenManifestState));
        OnPropertyChanged(nameof(GenPackageState));
        OnPropertyChanged(nameof(GenBuildState));
        OnPropertyChanged(nameof(GenSignState));
        OnPropertyChanged(nameof(GenStageTitle));
    }

    // ── Editor wizard (guided steps) ──────────────────────────────────────
    // The edit screen is a Next/Back wizard. Step circles are also clickable.
    public static readonly string[] EditStepLabels = { "General", "Apps", "Signing", "Appearance", "Launch" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditStep1State))]
    [NotifyPropertyChangedFor(nameof(EditStep2State))]
    [NotifyPropertyChangedFor(nameof(EditStep3State))]
    [NotifyPropertyChangedFor(nameof(EditStep4State))]
    [NotifyPropertyChangedFor(nameof(EditStep5State))]
    [NotifyPropertyChangedFor(nameof(EditStepTitle))]
    [NotifyPropertyChangedFor(nameof(IsFirstStep))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    [NotifyPropertyChangedFor(nameof(ShowSaveHint))]
    [NotifyPropertyChangedFor(nameof(IsGeneralStep))]
    [NotifyPropertyChangedFor(nameof(IsAppsStep))]
    [NotifyPropertyChangedFor(nameof(IsSigningStep))]
    [NotifyPropertyChangedFor(nameof(IsAppearanceStep))]
    [NotifyPropertyChangedFor(nameof(IsLaunchStep))]
    [NotifyCanExecuteChangedFor(nameof(NextStepCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrevStepCommand))]
    private int _editStepIndex;

    public bool IsFirstStep => EditStepIndex <= 0;
    public bool IsLastStep  => EditStepIndex >= EditStepLabels.Length - 1;

    public bool IsGeneralStep    => EditStepIndex == 0;
    public bool IsAppsStep       => EditStepIndex == 1;
    public bool IsSigningStep    => EditStepIndex == 2;
    public bool IsAppearanceStep => EditStepIndex == 3;
    public bool IsLaunchStep     => EditStepIndex == 4;
    public string EditStepTitle =>
        $"Step {EditStepIndex + 1} of {EditStepLabels.Length} · {EditStepLabels[EditStepIndex]}";

    private string EditStepState(int index) =>
        index < EditStepIndex ? "Done" : index == EditStepIndex ? "Current" : "Pending";

    public string EditStep1State => EditStepState(0);
    public string EditStep2State => EditStepState(1);
    public string EditStep3State => EditStepState(2);
    public string EditStep4State => EditStepState(3);
    public string EditStep5State => EditStepState(4);

    [RelayCommand(CanExecute = nameof(CanNextStep))]
    private void NextStep() { if (!IsLastStep) EditStepIndex++; }
    private bool CanNextStep() => !IsLastStep;

    [RelayCommand(CanExecute = nameof(CanPrevStep))]
    private void PrevStep() { if (!IsFirstStep) EditStepIndex--; }
    private bool CanPrevStep() => !IsFirstStep;

    [RelayCommand]
    private void GoToStep(object? index)
    {
        if (index is int i && i >= 0 && i < EditStepLabels.Length) EditStepIndex = i;
        else if (index is string s && int.TryParse(s, out var n) && n >= 0 && n < EditStepLabels.Length) EditStepIndex = n;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSetupCommand))]
    [NotifyPropertyChangedFor(nameof(ShowSaveHint))]
    private string _editName = string.Empty;

    [ObservableProperty] private string _editVersion = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSetupCommand))]
    [NotifyPropertyChangedFor(nameof(ShowSaveHint))]
    private string _editOutputFolder = string.Empty;

    /// <summary>Shown on the last step when Save is blocked, since the missing fields live on Step 1.</summary>
    public bool ShowSaveHint => IsLastStep && !CanSaveSetup();

    [ObservableProperty] private bool _editPreserveOldSetups;
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

    public ObservableCollection<SetupBundleVm> Bundles { get; } = [];
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
        // Tolerate duplicate ids (GroupBy) so a bad data set never throws here.
        var appsById = _storage.GetAll()
            .GroupBy(a => a.Id)
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var b in _setupStorage.GetAll())
            Bundles.Add(new SetupBundleVm(b, appsById));
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
        EditPreserveOldSetups = false;
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

        EditStepIndex = 0;
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
        EditPreserveOldSetups = bundle.PreserveOldSetups;
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

        EditStepIndex = 0;
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

    private bool CanSaveSetup() =>
        !string.IsNullOrWhiteSpace(EditName) && !string.IsNullOrWhiteSpace(EditOutputFolder);

    [RelayCommand(CanExecute = nameof(CanSaveSetup))]
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
        bundle.PreserveOldSetups = EditPreserveOldSetups;
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

    private CancellationTokenSource? _genCts;

    [RelayCommand]
    private async Task GenerateSetup(SetupBundle? bundle)
    {
        if (bundle is null) return;

        // Guard older/partial bundles that predate Save-time validation.
        if (string.IsNullOrWhiteSpace(bundle.OutputFolder))
        {
            _dialog.Alert("Cannot Generate", "This bundle has no output folder. Edit it and set one first.");
            return;
        }
        if (bundle.Apps.Count == 0)
        {
            _dialog.Alert("Cannot Generate", "This bundle has no apps. Edit it and add at least one.");
            return;
        }

        _genCts = new CancellationTokenSource();
        _lastGeneratedSetupPath = null;
        ShowGenerationOverlay = true;
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
                _setupService.GenerateAsync(bundle, progress, _genCts.Token), _genCts.Token);

            bundle.LastGeneratedPath = setupPath;
            bundle.LastGeneratedDate = DateTime.Now;
            // Snapshot the app versions that actually went into this build.
            bundle.GeneratedAppVersions = bundle.Apps
                .Where(a => !string.IsNullOrEmpty(a.AppId))
                .GroupBy(a => a.AppId)
                .ToDictionary(g => g.Key,
                    g => _storage.GetById(g.Key)?.LatestVersion?.VersionNumber ?? string.Empty);
            _setupStorage.Save(bundle);
            Reload();
            ReloadHistory();

            _log.Write("Setups", $"Setup generated: {setupPath}");
            _lastGeneratedSetupPath = setupPath;
            GenerationSucceeded = true;
            GenerationResultDetail = setupPath;
        }
        catch (OperationCanceledException)
        {
            _log.Write("SetupGen", "Generation cancelled by user.");
            ShowGenerationOverlay = false;   // nothing to report — just close
        }
        catch (Exception ex)
        {
            _log.Write("SetupGen", $"Generation failed: {ex.Message}");
            GenerationSucceeded = false;
            GenerationResultDetail = ex.Message;
        }
        finally
        {
            IsGenerating = false;            // clears the running phase → result phase shows
            _genCts.Dispose();
            _genCts = null;
        }
    }

    [RelayCommand]
    private void CancelGeneration()
    {
        if (_genCts is null || _genCts.IsCancellationRequested) return;
        GenerationMessage = "Cancelling…";
        _genCts.Cancel();
    }

    [RelayCommand]
    private void CloseGenerationResult() => ShowGenerationOverlay = false;

    [RelayCommand]
    private void OpenGeneratedFolder() => RevealInExplorer(_lastGeneratedSetupPath);
}

/// <summary>Display wrapper for a setup bundle in the list — resolves app names/versions and status.</summary>
public class SetupBundleVm
{
    public SetupBundle Bundle { get; }

    public SetupBundleVm(SetupBundle bundle, IReadOnlyDictionary<string, AppEntry> appsById)
    {
        Bundle = bundle;

        var generatedVersions = bundle.GeneratedAppVersions ?? new Dictionary<string, string>();

        var parts = new List<string>();
        foreach (var a in bundle.Apps ?? new List<SetupAppRef>())
        {
            if (a is null || string.IsNullOrEmpty(a.AppId) || !appsById.TryGetValue(a.AppId, out var app))
            {
                parts.Add("(removed app)");
                continue;
            }
            // Prefer the version captured when the setup was generated; fall back to the app's current
            // latest (for bundles not generated yet, or apps added since the last build).
            var v = generatedVersions.TryGetValue(a.AppId, out var gv) && !string.IsNullOrEmpty(gv)
                ? gv
                : app.LatestVersion?.VersionNumber;
            var versioned = string.IsNullOrEmpty(v) ? app.Name : $"{app.Name} v{v}";
            // Show the version for both modes; note cumulative bundles (all versions through latest).
            parts.Add(a.VersionMode == VersionMode.Cumulative ? $"{versioned} (cumulative)" : versioned);
        }
        AppsSummary = parts.Count > 0 ? string.Join("   ·   ", parts) : "No apps selected";
    }

    public string  Name              => Bundle.Name;
    public string  Version           => Bundle.Version;
    public int     AppsCount         => Bundle.Apps?.Count ?? 0;
    public DateTime CreatedDate      => Bundle.CreatedDate;
    public DateTime? LastGeneratedDate => Bundle.LastGeneratedDate;
    public string? LastGeneratedPath => Bundle.LastGeneratedPath;
    public bool    IsSigned          => Bundle.SignOutput;
    public bool    IsGenerated       => Bundle.LastGeneratedDate is not null;
    public string  AppsSummary       { get; }

    /// <summary>The generated file path if it exists, otherwise the configured output folder.</summary>
    public string  OutputDisplay     => string.IsNullOrWhiteSpace(Bundle.LastGeneratedPath)
        ? Bundle.OutputFolder
        : Bundle.LastGeneratedPath;
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
