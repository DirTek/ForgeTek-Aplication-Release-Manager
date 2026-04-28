using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class ScanViewModel : ObservableObject
{
    private readonly AppEntry _entry;
    private readonly MainViewModel _main;
    private readonly StorageService _storage;

    public string AppName => _entry.Name;
    public string AppPath => _entry.FolderPath;
    public int FileCount { get; }

    public ObservableCollection<FileTreeNodeViewModel> FileTree { get; }

    [ObservableProperty] private string _versionNumber = string.Empty;

    public ScanViewModel(AppEntry entry, IReadOnlyList<FileRecord> files,
        MainViewModel main, StorageService storage, string initialVersion = "")
    {
        _entry = entry;
        _main = main;
        _storage = storage;
        FileCount = files.Count;
        FileTree = FileTreeNodeViewModel.BuildScanTree(files);
        VersionNumber = initialVersion;
    }

    [RelayCommand]
    private void Approve()
    {
        if (string.IsNullOrWhiteSpace(VersionNumber))
        {
            System.Windows.MessageBox.Show("Please enter a version number.", "Missing Version",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        var version = new AppVersion
        {
            VersionNumber = VersionNumber.Trim(),
            ScanDate = DateTime.Now,
            Files = FileTreeNodeViewModel.CollectFiles(FileTree).ToList(),
        };

        _entry.Versions.Add(version);
        _main.NavigateToDetail(_entry);
    }

    [RelayCommand]
    private void Cancel() => _main.NavigateToDetail(_entry);
}
