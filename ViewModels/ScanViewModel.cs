using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Dialogs;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class ScanViewModel : ObservableObject
{
    private readonly AppEntry _entry;
    private readonly MainViewModel _main;
    private readonly StorageService _storage;
    private readonly IReadOnlyList<FileRecord> _allFiles;

    private readonly string? _detectedExeVersion;

    public string AppName => _entry.Name;
    public string AppPath => _entry.FolderPath;
    public int FileCount { get; }

    public bool HasVersionMismatch =>
        _detectedExeVersion is not null &&
        !string.Equals(VersionNumber.Trim(), _detectedExeVersion, StringComparison.OrdinalIgnoreCase);

    public string VersionMismatchText => $"EXE file version is v{_detectedExeVersion}";

    public ObservableCollection<FileTreeNodeViewModel> FileTree { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVersionMismatch))]
    private string _versionNumber = string.Empty;

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

    public ScanViewModel(AppEntry entry, IReadOnlyList<FileRecord> files,
        MainViewModel main, StorageService storage,
        string initialVersion = "", string? detectedVersion = null)
    {
        _entry = entry;
        _main = main;
        _storage = storage;
        _allFiles = files;
        FileCount = files.Count;
        _detectedExeVersion = detectedVersion;
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
        // Preserve per-file inclusion state across sort changes
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
            new AlertDialog("Missing Version", "Please enter a version number.")
                { Owner = Application.Current.MainWindow }.ShowDialog();
            return;
        }

        var trimmed = VersionNumber.Trim();

        if (_entry.Versions.Any(v => v.VersionNumber == trimmed))
        {
            var warn = new ConfirmDialog(
                "Duplicate Version",
                $"Version {trimmed} has already been scanned for this app. Saving it again may cause update loops.\n\nSave anyway?",
                "Save Anyway")
            { Owner = Application.Current.MainWindow };
            if (warn.ShowDialog() != true) return;
        }

        if (_detectedExeVersion is not null &&
            Version.TryParse(trimmed, out var typedVer) &&
            Version.TryParse(_detectedExeVersion, out var exeVer) &&
            typedVer > exeVer)
        {
            var warn = new ConfirmDialog(
                "Update Loop Risk",
                $"You are packaging version {trimmed}, but the EXE reports v{_detectedExeVersion}.\n\n" +
                $"Clients will see {trimmed} in the catalog, check their installed EXE ({_detectedExeVersion}), " +
                $"download the update — and then loop forever because the EXE version never changes.\n\nSave anyway?",
                "Save Anyway")
            { Owner = Application.Current.MainWindow };
            if (warn.ShowDialog() != true) return;
        }

        var version = new AppVersion
        {
            VersionNumber = trimmed,
            ScanDate      = DateTime.Now,
            IsInitial     = true,
            Files         = FileTreeNodeViewModel.CollectFiles(FileTree).ToList(),
        };

        _entry.Versions.Add(version);
        _main.NavigateToDetail(_entry);
    }

    [RelayCommand]
    private void Cancel() => _main.NavigateToDetail(_entry);
}
