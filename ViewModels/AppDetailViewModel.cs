using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Dialogs;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class AppDetailViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly StorageService _storage;
    private readonly ScannerService _scanner;

    public AppEntry Entry { get; }
    public string AppName => Entry.Name;
    public string AppPath => Entry.FolderPath;
    public ObservableCollection<AppVersion> Versions { get; }

    [ObservableProperty] private bool _isScanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ViewVersionDiffCommand))]
    private AppVersion? _selectedVersion;

    public AppDetailViewModel(AppEntry entry, MainViewModel main, StorageService storage, ScannerService scanner)
    {
        Entry = entry;
        _main = main;
        _storage = storage;
        _scanner = scanner;
        Versions = new ObservableCollection<AppVersion>(entry.Versions.AsEnumerable().Reverse());
    }

    private bool CanViewDiff()
    {
        if (SelectedVersion is null) return false;
        var idx = Entry.Versions.IndexOf(SelectedVersion);
        return idx > 0; // has a previous version to diff against
    }

    [RelayCommand(CanExecute = nameof(CanViewDiff))]
    private void ViewVersionDiff()
    {
        var idx = Entry.Versions.IndexOf(SelectedVersion!);
        var baseVersion = Entry.Versions[idx - 1];
        var diff = _scanner.DiffVersions(baseVersion, SelectedVersion!.Files);
        _main.NavigateToVersionDiff(Entry, SelectedVersion!, diff);
    }

    [RelayCommand]
    private async Task ScanNow()
    {
        if (!System.IO.Directory.Exists(Entry.FolderPath))
        {
            System.Windows.MessageBox.Show(
                $"Folder not found:\n{Entry.FolderPath}",
                "Scan Error", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        IsScanning = true;
        IReadOnlyList<FileRecord> files;
        try
        {
            files = await Task.Run(() => _scanner.ScanDirectory(Entry.FolderPath));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Scan Error",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            IsScanning = false;
            return;
        }
        IsScanning = false;

        if (Entry.LatestVersion is { } latest)
            _main.NavigateToDiff(Entry, files, _scanner.DiffVersions(latest, files));
        else
            _main.NavigateToScan(Entry, files);
    }

    [RelayCommand]
    private void EditApp()
    {
        var dlg = new AddEditAppDialog(Entry.Name, Entry.FolderPath)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (dlg.ShowDialog() != true) return;
        Entry.Name = dlg.AppName;
        Entry.FolderPath = dlg.AppPath;
        _main.NavigateToDetail(Entry);
    }

    [RelayCommand]
    private void DeleteApp()
    {
        var r = System.Windows.MessageBox.Show(
            $"Delete '{Entry.Name}' and all its version history?",
            "Confirm Delete",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (r != System.Windows.MessageBoxResult.Yes) return;
        _storage.Delete(Entry.Id);
        _main.NavigateToWelcome();
    }
}
