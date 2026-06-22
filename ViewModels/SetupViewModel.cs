using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Dialogs;
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
    private readonly IWingetManifestService _winget;
    private readonly Services.Publishing.IPublishService _publish;
    private readonly IApprovalService _approval;
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
    public static readonly string[] EditStepLabels = { "General", "Apps", "Signing", "Appearance", "Launch", "Actions" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditStep1State))]
    [NotifyPropertyChangedFor(nameof(EditStep2State))]
    [NotifyPropertyChangedFor(nameof(EditStep3State))]
    [NotifyPropertyChangedFor(nameof(EditStep4State))]
    [NotifyPropertyChangedFor(nameof(EditStep5State))]
    [NotifyPropertyChangedFor(nameof(EditStep6State))]
    [NotifyPropertyChangedFor(nameof(EditStepTitle))]
    [NotifyPropertyChangedFor(nameof(IsFirstStep))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    [NotifyPropertyChangedFor(nameof(ShowSaveHint))]
    [NotifyPropertyChangedFor(nameof(IsGeneralStep))]
    [NotifyPropertyChangedFor(nameof(IsAppsStep))]
    [NotifyPropertyChangedFor(nameof(IsSigningStep))]
    [NotifyPropertyChangedFor(nameof(IsAppearanceStep))]
    [NotifyPropertyChangedFor(nameof(IsLaunchStep))]
    [NotifyPropertyChangedFor(nameof(IsActionsStep))]
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
    public bool IsActionsStep    => EditStepIndex == 5;
    public string EditStepTitle =>
        $"Step {EditStepIndex + 1} of {EditStepLabels.Length} · {EditStepLabels[EditStepIndex]}";

    private string EditStepState(int index) =>
        index < EditStepIndex ? "Done" : index == EditStepIndex ? "Current" : "Pending";

    public string EditStep1State => EditStepState(0);
    public string EditStep2State => EditStepState(1);
    public string EditStep3State => EditStepState(2);
    public string EditStep4State => EditStepState(3);
    public string EditStep5State => EditStepState(4);
    public string EditStep6State => EditStepState(5);

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

    [ObservableProperty] private string _editFileNameTemplate = string.Empty;
    [ObservableProperty] private bool _editGeneratePortableZip;

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

    // ── Setup color theme + button style (blank = keep the installer's dark default) ──
    [ObservableProperty] private string _editAccentColor = string.Empty;
    [ObservableProperty] private string _editAccentHoverColor = string.Empty;
    [ObservableProperty] private string _editButtonTextColor = string.Empty;
    [ObservableProperty] private string _editTextColor = string.Empty;
    [ObservableProperty] private string _editSurfaceColor = string.Empty;
    [ObservableProperty] private string _editButtonShape = "Rounded";

    public string[] BackgroundModes { get; } = ["Default", "Solid", "Gradient", "Image"];
    public string[] GradientDirections { get; } = ["Vertical", "Horizontal", "Diagonal"];
    public string[] ButtonShapes { get; } = ["Rounded", "Square", "Pill"];

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

    /// <summary>The bundle highlighted in the list; the header toolbar actions operate on it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private SetupBundleVm? _selectedBundle;

    public bool HasSelection => SelectedBundle is not null;
    public ObservableCollection<SetupBundleAppItem> AvailableApps { get; } = [];
    public ObservableCollection<RedistItem> WorkingRedists { get; } = [];

    // Custom install actions (bundle-level, pre/post). Authored on the "Actions" wizard step.
    public ObservableCollection<CustomActionItem> CustomActions { get; } = [];
    public string[] ActionTypeOptions { get; } =
        Enum.GetNames<CustomActionType>();
    public string[] ActionTimingOptions { get; } =
        Enum.GetNames<CustomActionTiming>();

    // Finish-page actions (open website / readme) shown as toggleable checkboxes. Authored on "Launch".
    public ObservableCollection<CompletionActionItem> CompletionActions { get; } = [];
    public string[] CompletionActionKinds { get; } =
        Enum.GetNames<CompletionActionKind>();

    public SetupViewModel(ISetupStorageService setupStorage, IStorageService storage,
        ISetupService setupService, IDialogService dialog, ILogService log,
        ISettingsService settings, IWingetManifestService winget,
        Services.Publishing.IPublishService publish, IApprovalService approval)
    {
        _setupStorage = setupStorage;
        _storage = storage;
        _setupService = setupService;
        _dialog = dialog;
        _log = log;
        _settings = settings;
        _winget = winget;
        _publish = publish;
        _approval = approval;
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
        var history = _setupStorage.GetHistory();
        foreach (var b in _setupStorage.GetAll())
        {
            // The "Published" badge reflects the CURRENT (latest generated) setup — not some older build.
            // Generating a new setup that hasn't been published clears the badge.
            var latest = history
                .Where(r => r.BundleId == b.Id)
                .OrderByDescending(r => r.GeneratedDate)
                .FirstOrDefault();
            var lastPublished = latest is not null && !string.IsNullOrWhiteSpace(latest.PublishedUrl)
                ? latest : null;
            Bundles.Add(new SetupBundleVm(b, appsById, lastPublished));
        }
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
    private void AddCustomAction()
        => CustomActions.Add(new CustomActionItem());

    [RelayCommand]
    private void RemoveCustomAction(CustomActionItem? item)
    {
        if (item is not null) CustomActions.Remove(item);
    }

    [RelayCommand]
    private void BrowseActionTarget(CustomActionItem? item)
    {
        if (item is null) return;
        var path = _dialog.OpenFile("Select the script or executable to run",
            "Scripts & executables (*.ps1;*.exe;*.bat;*.cmd)|*.ps1;*.exe;*.bat;*.cmd|All files (*.*)|*.*");
        if (path is not null) item.Target = path;
    }

    [RelayCommand]
    private void AddCompletionAction()
        => CompletionActions.Add(new CompletionActionItem());

    [RelayCommand]
    private void RemoveCompletionAction(CompletionActionItem? item)
    {
        if (item is not null) CompletionActions.Remove(item);
    }

    [RelayCommand]
    private void BrowseCompletionTarget(CompletionActionItem? item)
    {
        if (item is null) return;
        var path = _dialog.OpenFile("Select the file to open after install",
            "Documents (*.txt;*.md;*.pdf;*.html;*.htm)|*.txt;*.md;*.pdf;*.html;*.htm|All files (*.*)|*.*");
        if (path is not null) item.Target = path;
    }

    // Trims a value, returning null when blank (keeps optional theme colors out of the bundle).
    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
        EditFileNameTemplate = string.Empty;
        EditGeneratePortableZip = false;
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
        EditAccentColor = string.Empty;
        EditAccentHoverColor = string.Empty;
        EditButtonTextColor = string.Empty;
        EditTextColor = string.Empty;
        EditSurfaceColor = string.Empty;
        EditButtonShape = "Rounded";
        AvailableApps.Clear();
        WorkingRedists.Clear();
        CustomActions.Clear();
        CompletionActions.Clear();

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
            IsOptional = existing?.IsOptional ?? false,
            DefaultSelected = existing?.DefaultSelected ?? true,
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
        EditFileNameTemplate = bundle.FileNameTemplate ?? string.Empty;
        EditGeneratePortableZip = bundle.GeneratePortableZip;
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
        EditAccentColor = bundle.AccentColor ?? string.Empty;
        EditAccentHoverColor = bundle.AccentHoverColor ?? string.Empty;
        EditButtonTextColor = bundle.ButtonTextColor ?? string.Empty;
        EditTextColor = bundle.TextColor ?? string.Empty;
        EditSurfaceColor = bundle.SurfaceColor ?? string.Empty;
        EditButtonShape = string.IsNullOrWhiteSpace(bundle.ButtonShape) ? "Rounded" : bundle.ButtonShape;
        AvailableApps.Clear();
        WorkingRedists.Clear();
        CustomActions.Clear();
        foreach (var a in bundle.CustomActions)
            CustomActions.Add(new CustomActionItem
            {
                Type = a.Type.ToString(),
                Timing = a.Timing.ToString(),
                Label = a.Label,
                Target = a.Target,
                Arguments = a.Arguments,
                InlineScript = a.InlineScript,
                IgnoreFailure = a.IgnoreFailure,
                TimeoutSeconds = a.TimeoutSeconds,
            });

        CompletionActions.Clear();
        foreach (var a in bundle.CompletionActions)
            CompletionActions.Add(new CompletionActionItem
            {
                Kind = a.Kind.ToString(),
                Label = a.Label,
                Target = a.Target,
                DefaultChecked = a.DefaultChecked,
            });

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
                IsOptional = a.IsOptional,
                DefaultSelected = a.DefaultSelected,
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
        bundle.FileNameTemplate = string.IsNullOrWhiteSpace(EditFileNameTemplate) ? null : EditFileNameTemplate.Trim();
        bundle.GeneratePortableZip = EditGeneratePortableZip;
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
        bundle.AccentColor = NullIfBlank(EditAccentColor);
        bundle.AccentHoverColor = NullIfBlank(EditAccentHoverColor);
        bundle.ButtonTextColor = NullIfBlank(EditButtonTextColor);
        bundle.TextColor = NullIfBlank(EditTextColor);
        bundle.SurfaceColor = NullIfBlank(EditSurfaceColor);
        bundle.ButtonShape = string.IsNullOrWhiteSpace(EditButtonShape) ? "Rounded" : EditButtonShape;
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

        bundle.CustomActions = CustomActions
            .Where(a => !string.IsNullOrWhiteSpace(a.Target) || !string.IsNullOrWhiteSpace(a.InlineScript))
            .Select(a => new SetupCustomAction
            {
                Type = Enum.TryParse<CustomActionType>(a.Type, out var t) ? t : CustomActionType.RunPowerShell,
                Timing = Enum.TryParse<CustomActionTiming>(a.Timing, out var tm) ? tm : CustomActionTiming.PostInstall,
                Label = a.Label?.Trim() ?? string.Empty,
                Target = a.Target?.Trim() ?? string.Empty,
                Arguments = a.Arguments?.Trim() ?? string.Empty,
                InlineScript = a.InlineScript ?? string.Empty,
                IgnoreFailure = a.IgnoreFailure,
                TimeoutSeconds = a.TimeoutSeconds < 0 ? 0 : a.TimeoutSeconds,
            })
            .ToList();

        bundle.CompletionActions = CompletionActions
            .Where(a => !string.IsNullOrWhiteSpace(a.Target))
            .Select(a => new SetupCompletionAction
            {
                Kind = Enum.TryParse<CompletionActionKind>(a.Kind, out var k) ? k : CompletionActionKind.OpenUrl,
                Label = a.Label?.Trim() ?? string.Empty,
                Target = a.Target?.Trim() ?? string.Empty,
                DefaultChecked = a.DefaultChecked,
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

    /// <summary>Opens the winget-manifest dialog for a generated setup. From the success overlay no
    /// record is passed (the newest history entry is used); from a Past Bundles card the record is bound.</summary>
    [RelayCommand]
    private void GenerateWingetManifest(GeneratedSetupRecord? record)
    {
        record ??= _setupStorage.GetHistory()
            .FirstOrDefault(r => string.Equals(r.OutputPath, _lastGeneratedSetupPath, StringComparison.OrdinalIgnoreCase))
            ?? _setupStorage.GetHistory().FirstOrDefault();
        if (record is null)
        {
            _dialog.Alert("No Setup", "Generate a setup first, then create its winget manifest.");
            return;
        }
        if (!File.Exists(record.OutputPath) && string.IsNullOrWhiteSpace(record.Sha256))
        {
            _dialog.Alert("Setup Not Found",
                $"The setup file is missing and has no stored hash:\n{record.OutputPath}");
            return;
        }

        var appName = ResolvePrimaryAppName(record);
        var profile = ResolveBundle(record)?.PublishProfile;
        new WingetManifestWindow(_winget, _publish, _settings, appName, record, profile)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        }.ShowDialog();
    }

    /// <summary>Uploads a generated setup to the BUNDLE's own publish target (separate from the apps'
    /// update settings). From the success overlay no record is passed; from a Past Bundles card it's bound.</summary>
    [RelayCommand]
    private void PublishSetup(GeneratedSetupRecord? record)
    {
        record ??= _setupStorage.GetHistory()
            .FirstOrDefault(r => string.Equals(r.OutputPath, _lastGeneratedSetupPath, StringComparison.OrdinalIgnoreCase))
            ?? _setupStorage.GetHistory().FirstOrDefault();
        if (record is null)
        {
            _dialog.Alert("No Setup", "Generate a setup first, then publish it.");
            return;
        }
        if (!File.Exists(record.OutputPath))
        {
            _dialog.Alert("Setup Not Found", $"The setup file is missing:\n{record.OutputPath}");
            return;
        }

        var appKey = ResolvePrimaryAppName(record).ToLowerInvariant().Replace(" ", "");
        var profile = ResolveBundle(record)?.PublishProfile;
        var requireApproval = _settings.Global.RequireReleaseApproval;
        new PublishSetupWindow(_publish, _setupStorage, appKey, profile, record, _approval, _main?.Session, requireApproval)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        }.ShowDialog();

        // The record's publish status may have changed; refresh the bundle cards' "Published" badge
        // and (when viewing it) the Past Bundles list.
        Reload();
        if (IsPastCategory) ReloadHistory();
    }

    /// <summary>Publishes a bundle's most recently generated setup — straight from the bundle card,
    /// without re-generating. Falls back to a prompt when the bundle has never been generated.</summary>
    [RelayCommand]
    private void PublishBundleSetup(SetupBundleVm? vm)
    {
        var bundle = vm?.Bundle;
        if (bundle is null)
        {
            _dialog.Alert("No Bundle", "Select a setup bundle first.");
            return;
        }
        var forBundle = _setupStorage.GetHistory()
            .Where(r => r.BundleId == bundle.Id)
            .OrderByDescending(r => r.GeneratedDate)
            .ToList();
        // Prefer the newest build whose setup file still exists on disk.
        var record = forBundle.FirstOrDefault(r => File.Exists(r.OutputPath));
        if (record is null)
        {
            _dialog.Alert(forBundle.Count == 0 ? "No Setup Yet" : "Setup File Missing",
                forBundle.Count == 0
                    ? $"\"{bundle.Name}\" hasn't been generated yet. Generate it first, then publish."
                    : $"The generated setup file for \"{bundle.Name}\" is missing (moved or deleted). Re-generate it, then publish.");
            return;
        }
        PublishSetup(record);
    }

    /// <summary>Configures a bundle's own setup-publish target (separate from app update settings).</summary>
    [RelayCommand]
    private void ConfigureSetupPublish(SetupBundleVm? vm)
    {
        var bundle = vm?.Bundle ?? _editingBundle;
        if (bundle is null)
        {
            _dialog.Alert("No Bundle", "Select a setup bundle first.");
            return;
        }
        new SetupPublishSettingsWindow(_publish, _storage, _settings, _setupStorage, bundle)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        }.ShowDialog();
    }

    private SetupBundle? ResolveBundle(GeneratedSetupRecord record)
        => Bundles.FirstOrDefault(b => b.Bundle.Id == record.BundleId)?.Bundle;

    // The setup is built from a bundle that may reference several apps; winget identifies one package,
    // so we use the bundle's launch app (else its first app). Falls back to the bundle name.
    private string ResolvePrimaryAppName(GeneratedSetupRecord record)
    {
        var bundle = ResolveBundle(record);
        if (bundle is not null)
        {
            var appId = !string.IsNullOrWhiteSpace(bundle.LaunchAppId)
                ? bundle.LaunchAppId
                : bundle.Apps.FirstOrDefault()?.AppId;
            if (!string.IsNullOrWhiteSpace(appId) && _storage.GetById(appId!) is { } app)
                return app.Name;
        }
        return string.IsNullOrWhiteSpace(record.BundleName) ? "App" : record.BundleName;
    }
}

/// <summary>Display wrapper for a setup bundle in the list — resolves app names/versions and status.</summary>
public class SetupBundleVm
{
    public SetupBundle Bundle { get; }
    private readonly GeneratedSetupRecord? _lastPublished;

    public SetupBundleVm(SetupBundle bundle, IReadOnlyDictionary<string, AppEntry> appsById,
        GeneratedSetupRecord? lastPublished = null)
    {
        Bundle = bundle;
        _lastPublished = lastPublished;

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

    // ── Publish status (latest published build for this bundle) ──────────────
    public bool      IsPublished       => _lastPublished is not null;
    public string?   PublishedProvider => _lastPublished?.PublishedProvider;
    public DateTime? PublishedDate     => _lastPublished?.PublishedDate;
    public string?   PublishedUrl      => _lastPublished?.PublishedUrl;
    public string    PublishTooltip    => _lastPublished is null
        ? string.Empty
        : $"Published to {_lastPublished.PublishedProvider}"
          + (_lastPublished.PublishedDate is { } d ? $" on {d:yyyy-MM-dd HH:mm}" : "")
          + $"\n{_lastPublished.PublishedUrl}";

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
    /// <summary>When true, the end user can deselect this app during install.</summary>
    [ObservableProperty] private bool _isOptional;
    /// <summary>For optional apps, whether the install-time checkbox starts checked.</summary>
    [ObservableProperty] private bool _defaultSelected = true;

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

public partial class CustomActionItem : ObservableObject
{
    // Type/Timing are string-backed for easy ComboBox binding (enum names).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServiceAction))]
    [NotifyPropertyChangedFor(nameof(IsPowerShellAction))]
    [NotifyPropertyChangedFor(nameof(IsExecutableAction))]
    [NotifyPropertyChangedFor(nameof(IsDeleteAction))]
    [NotifyPropertyChangedFor(nameof(TargetLabel))]
    private string _type = nameof(CustomActionType.RunPowerShell);

    [ObservableProperty] private string _timing = nameof(CustomActionTiming.PostInstall);
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private string _target = string.Empty;
    [ObservableProperty] private string _arguments = string.Empty;
    [ObservableProperty] private string _inlineScript = string.Empty;
    [ObservableProperty] private bool _ignoreFailure;
    [ObservableProperty] private int _timeoutSeconds = 60;

    public bool IsServiceAction    => Type is nameof(CustomActionType.ServiceStop) or nameof(CustomActionType.ServiceStart);
    public bool IsPowerShellAction => Type == nameof(CustomActionType.RunPowerShell);
    public bool IsExecutableAction => Type == nameof(CustomActionType.RunExecutable);
    public bool IsDeleteAction     => Type == nameof(CustomActionType.DeleteFiles);

    /// <summary>Context-sensitive caption for the Target field.</summary>
    public string TargetLabel => Type switch
    {
        nameof(CustomActionType.ServiceStop) or nameof(CustomActionType.ServiceStart) => "Service name",
        nameof(CustomActionType.RunExecutable) => "Executable (bundled or absolute path)",
        nameof(CustomActionType.RunPowerShell) => "Script file (.ps1) — optional if using inline script",
        nameof(CustomActionType.DeleteFiles) => "Paths to delete (semicolon-separated; [InstallDir] allowed)",
        _ => "Target",
    };
}

public partial class CompletionActionItem : ObservableObject
{
    // Kind is string-backed for easy ComboBox binding (enum names: OpenUrl / OpenFile).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileAction))]
    [NotifyPropertyChangedFor(nameof(TargetLabel))]
    private string _kind = nameof(CompletionActionKind.OpenUrl);

    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private string _target = string.Empty;
    [ObservableProperty] private bool _defaultChecked = true;

    public bool IsFileAction => Kind == nameof(CompletionActionKind.OpenFile);

    /// <summary>Context-sensitive caption for the Target field.</summary>
    public string TargetLabel => IsFileAction
        ? "File to open ([InstallDir] allowed)"
        : "Website URL (https://…)";
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
