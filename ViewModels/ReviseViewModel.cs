using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Dialogs;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class ReviseViewModel : ObservableObject
{
    private MainViewModel _main = null!;
    private AppEntry _entry = null!;
    private AppVersion _version = null!;
    private Dictionary<string, bool> _originalDebugFlags = [];

    public string AppName      => _entry.Name;
    public string VersionNumber => _version.VersionNumber;
    public bool HasPackagingWarning =>
        _version.Status is VersionStatus.Signed or VersionStatus.Packed;

    [ObservableProperty] private ObservableCollection<FileTreeNodeViewModel> _fileTree = [];
    [ObservableProperty] private string _searchText   = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortByName))]
    [NotifyPropertyChangedFor(nameof(IsSortByDate))]
    private SortMode _sortMode = SortMode.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterNonDebug))]
    [NotifyPropertyChangedFor(nameof(IsFilterDebugOnly))]
    private DebugFilter _debugFilter = DebugFilter.All;

    public bool IsSortByName    => SortMode    == SortMode.Name;
    public bool IsSortByDate    => SortMode    == SortMode.Date;
    public bool IsFilterAll      => DebugFilter == DebugFilter.All;
    public bool IsFilterNonDebug => DebugFilter == DebugFilter.NonDebug;
    public bool IsFilterDebugOnly => DebugFilter == DebugFilter.DebugOnly;

    public void Initialize(AppEntry entry, AppVersion version, MainViewModel main)
    {
        _entry   = entry;
        _version = version;
        _main    = main;
        _originalDebugFlags = version.Files.ToDictionary(f => f.Path, f => f.IsDebug);
        FileTree = FileTreeNodeViewModel.BuildScanTree(version.Files, SortMode);
    }

    partial void OnSearchTextChanged(string value)    => ApplyCurrentFilters();
    partial void OnSortModeChanged(SortMode value)    => RebuildTree();
    partial void OnDebugFilterChanged(DebugFilter value) => ApplyCurrentFilters();

    [RelayCommand] private void SortByName()      => SortMode    = SortMode.Name;
    [RelayCommand] private void SortByDate()      => SortMode    = SortMode.Date;
    [RelayCommand] private void FilterAll()       => DebugFilter = DebugFilter.All;
    [RelayCommand] private void FilterNonDebug()  => DebugFilter = DebugFilter.NonDebug;
    [RelayCommand] private void FilterDebugOnly() => DebugFilter = DebugFilter.DebugOnly;

    private void ApplyCurrentFilters()
        => FileTreeNodeViewModel.ApplyFilters(FileTree, SearchText?.Trim() ?? string.Empty, DebugFilter);

    private void RebuildTree()
    {
        FileTree = FileTreeNodeViewModel.BuildScanTree(_version.Files, SortMode);
        ApplyCurrentFilters();
    }

    [RelayCommand]
    private void Save()
    {
        if (HasPackagingWarning)
        {
            var dlg = new ConfirmDialog(
                "Reset Packaging?",
                $"v{VersionNumber} has already started packaging. Saving will reset it to Review status — the manifest and package will need to be regenerated.\n\nContinue?",
                "Reset & Save")
            { Owner = System.Windows.Application.Current.MainWindow };

            if (dlg.ShowDialog() != true) return;

            _version.Status       = VersionStatus.Review;
            _version.HasManifest  = false;
            _version.HasPackage   = false;
            _version.PackagePath  = null;
            _version.PipelineStep = null;
        }

        _main.NavigateToDetail(_entry);
    }

    [RelayCommand]
    private void Cancel()
    {
        foreach (var file in _version.Files)
        {
            if (_originalDebugFlags.TryGetValue(file.Path, out var original))
                file.IsDebug = original;
        }
        _main.NavigateToDetail(_entry);
    }
}
