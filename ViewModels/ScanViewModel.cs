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

    public string AppName => _entry.Name;
    public string AppPath => _entry.FolderPath;
    public int FileCount { get; }

    public ObservableCollection<FileTreeNodeViewModel> FileTree { get; } = [];

    [ObservableProperty] private string _versionNumber = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortByName))]
    [NotifyPropertyChangedFor(nameof(IsSortByDate))]
    private SortMode _sortMode = SortMode.Name;

    [ObservableProperty] private string _searchText = string.Empty;

    public bool IsSortByName => SortMode == SortMode.Name;
    public bool IsSortByDate => SortMode == SortMode.Date;

    public ScanViewModel(AppEntry entry, IReadOnlyList<FileRecord> files,
        MainViewModel main, StorageService storage, string initialVersion = "")
    {
        _entry = entry;
        _main = main;
        _storage = storage;
        _allFiles = files;
        FileCount = files.Count;
        VersionNumber = initialVersion;

        foreach (var node in FileTreeNodeViewModel.BuildScanTree(_allFiles, SortMode))
            FileTree.Add(node);
    }

    partial void OnSortModeChanged(SortMode value) => RebuildTree();

    partial void OnSearchTextChanged(string value) =>
        FileTreeNodeViewModel.ApplySearch(FileTree, value.Trim());

    [RelayCommand]
    private void SortByName() => SortMode = SortMode.Name;

    [RelayCommand]
    private void SortByDate() => SortMode = SortMode.Date;

    private void RebuildTree()
    {
        // Preserve per-file inclusion state across sort changes
        var saved = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        SaveInclusion(FileTree, saved);

        var rebuilt = FileTreeNodeViewModel.BuildScanTree(_allFiles, SortMode);
        RestoreInclusion(rebuilt, saved);

        FileTree.Clear();
        foreach (var node in rebuilt) FileTree.Add(node);

        // Reapply any active search
        var search = SearchText?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(search))
            FileTreeNodeViewModel.ApplySearch(FileTree, search);
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

        var version = new AppVersion
        {
            VersionNumber = VersionNumber.Trim(),
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
