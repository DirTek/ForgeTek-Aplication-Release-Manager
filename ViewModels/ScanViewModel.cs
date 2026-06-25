using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekApplicationReleaseManager.Dialogs;
using ForgeTekApplicationReleaseManager.Models;
using ForgeTekApplicationReleaseManager.Services;

namespace ForgeTekApplicationReleaseManager.ViewModels;

public partial class ScanViewModel : ObservableObject
{
    private MainViewModel _main = null!;
    private readonly IStorageService _storage;
    private readonly IDialogService _dialog;
    private readonly ILocalizationService _loc;
    private readonly IUpdaterService _updater;
    private readonly ISettingsService _settings;
    private AppEntry _entry = null!;
    private IReadOnlyList<FileRecord> _allFiles = [];

    private string? _detectedExeVersion;

    public string AppName => _entry.Name;
    public string AppPath => _entry.FolderPath;
    public int FileCount { get; private set; }

    public bool HasVersionMismatch =>
        _detectedExeVersion is not null &&
        !string.Equals(VersionNumber.Trim(), _detectedExeVersion, StringComparison.OrdinalIgnoreCase);

    public string VersionMismatchText => _loc.Get("Str.DiffVm.VersionMismatch", _detectedExeVersion ?? string.Empty);

    public ObservableCollection<FileTreeNodeViewModel> FileTree { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVersionMismatch))]
    private string _versionNumber = string.Empty;

    /// <summary>When checked, this version is published to the Beta channel (pre-release).</summary>
    [ObservableProperty] private bool _isBeta;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortByName))]
    [NotifyPropertyChangedFor(nameof(IsSortByDate))]
    private SortMode _sortMode = SortMode.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterNonDebug))]
    [NotifyPropertyChangedFor(nameof(IsFilterDebugOnly))]
    private DebugFilter _debugFilter = DebugFilter.All;

    [ObservableProperty] private string _searchText = string.Empty;

    public bool IsSortByName    => SortMode    == SortMode.Name;
    public bool IsSortByDate    => SortMode    == SortMode.Date;
    public bool IsFilterAll      => DebugFilter == DebugFilter.All;
    public bool IsFilterNonDebug => DebugFilter == DebugFilter.NonDebug;
    public bool IsFilterDebugOnly => DebugFilter == DebugFilter.DebugOnly;

    public ScanViewModel(IStorageService storage, IDialogService dialog, ILocalizationService loc,
        IUpdaterService updater, ISettingsService settings)
    {
        _storage = storage;
        _dialog = dialog;
        _loc = loc;
        _updater = updater;
        _settings = settings;
    }

    public void Initialize(AppEntry entry, IReadOnlyList<FileRecord> files,
        MainViewModel main, string initialVersion = "", string? detectedVersion = null)
    {
        _entry = entry;
        _main = main;
        _allFiles = files;
        _detectedExeVersion = detectedVersion;
        FileCount = files.Count;
        VersionNumber = detectedVersion ?? initialVersion;

        foreach (var node in FileTreeNodeViewModel.BuildScanTree(_allFiles, SortMode))
            FileTree.Add(node);
    }

    partial void OnSortModeChanged(SortMode value)    => RebuildTree();
    partial void OnDebugFilterChanged(DebugFilter value) => ApplyCurrentFilters();

    partial void OnSearchTextChanged(string value) => ApplyCurrentFilters();

    [RelayCommand] private void SortByName()      => SortMode    = SortMode.Name;
    [RelayCommand] private void SortByDate()      => SortMode    = SortMode.Date;
    [RelayCommand] private void FilterAll()       => DebugFilter = DebugFilter.All;
    [RelayCommand] private void FilterNonDebug()  => DebugFilter = DebugFilter.NonDebug;
    [RelayCommand] private void FilterDebugOnly() => DebugFilter = DebugFilter.DebugOnly;

    private void ApplyCurrentFilters()
        => FileTreeNodeViewModel.ApplyFilters(FileTree, SearchText?.Trim() ?? string.Empty, DebugFilter);

    private void RebuildTree()
    {
        var saved = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        SaveInclusion(FileTree, saved);

        var rebuilt = FileTreeNodeViewModel.BuildScanTree(_allFiles, SortMode);
        RestoreInclusion(rebuilt, saved);

        FileTree.Clear();
        foreach (var node in rebuilt) FileTree.Add(node);

        ApplyCurrentFilters();
    }

    private static void SaveInclusion(IEnumerable<FileTreeNodeViewModel> nodes,
        Dictionary<string, bool> saved)
    {
        foreach (var n in nodes)
        {
            if (!n.IsFolder) saved[n.RelativePath] = n.IsIncluded;
            else SaveInclusion(n.Children, saved);
        }
    }

    private static void RestoreInclusion(IEnumerable<FileTreeNodeViewModel> nodes,
        Dictionary<string, bool> saved)
    {
        foreach (var n in nodes)
        {
            if (!n.IsFolder && saved.TryGetValue(n.RelativePath, out var inc))
                n.IsIncluded = inc;
            else if (n.IsFolder)
                RestoreInclusion(n.Children, saved);
        }
    }

    [RelayCommand]
    private void Approve()
    {
        if (string.IsNullOrWhiteSpace(VersionNumber))
        {
            _dialog.Alert(_loc.Get("Str.DiffVm.MissingVersionTitle"), _loc.Get("Str.DiffVm.MissingVersionMsg"));
            return;
        }

        var trimmed = VersionNumber.Trim();

        if (_entry.Versions.Any(v => v.VersionNumber == trimmed))
        {
            if (!_dialog.Confirm(_loc.Get("Str.DiffVm.DupTitle"),
                    _loc.Get("Str.DiffVm.DupMsg", trimmed),
                    _loc.Get("Str.Common.SaveAnyway"))) return;
        }

        if (_detectedExeVersion is not null &&
            Version.TryParse(trimmed, out var typedVer) &&
            Version.TryParse(_detectedExeVersion, out var exeVer) &&
            typedVer > exeVer)
        {
            if (!_dialog.Confirm(_loc.Get("Str.DiffVm.LoopTitle"),
                    _loc.Get("Str.DiffVm.LoopMsg", trimmed, _detectedExeVersion),
                    _loc.Get("Str.Common.SaveAnyway"))) return;
        }

        var version = new AppVersion
        {
            VersionNumber = trimmed,
            ScanDate      = DateTime.Now,
            IsInitial     = true,
            Channel       = IsBeta ? UpdateChannel.Beta : UpdateChannel.Stable,
            Files         = FileTreeNodeViewModel.CollectFiles(FileTree).ToList(),
        };

        _entry.Versions.Add(version);
        _main.NavigateToDetail(_entry);
        PromptGenerateUpdaterIfMissing(version);
        _main.PromptChangelogIfNeeded(_entry, trimmed);
    }

    // On the initial scan, offer to generate an updater when the app doesn't already ship one.
    private void PromptGenerateUpdaterIfMissing(AppVersion version)
    {
        if (version.Files.Any(IsUpdaterFile)) return;

        if (!_dialog.Confirm(_loc.Get("Str.ScanVm.NoUpdaterTitle"),
                _loc.Get("Str.ScanVm.NoUpdaterMsg"),
                _loc.Get("Str.GenUpdater.Generate"))) return;

        new GenerateUpdaterWindow(_entry, _updater, _settings, _storage)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        }.ShowDialog();
    }

    private static bool IsUpdaterFile(FileRecord f)
    {
        var name = System.IO.Path.GetFileName(f.Path);
        return name.EndsWith("updater.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("updater.json", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void Cancel() => _main.NavigateToDetail(_entry);
}
