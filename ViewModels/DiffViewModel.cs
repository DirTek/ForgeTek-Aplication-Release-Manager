using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class DiffViewModel : ObservableObject
{
    private readonly AppEntry _entry;
    private readonly IReadOnlyList<FileRecord> _scannedFiles;
    private readonly MainViewModel _main;
    private readonly StorageService _storage;
    private readonly AppVersion? _viewingVersion;

    public bool IsReadOnly { get; }
    public bool IsApprovalMode => !IsReadOnly;

    public string AppName => _entry.Name;

    public string ViewTitle => IsReadOnly && _viewingVersion is not null
        ? $"v{_viewingVersion.VersionNumber} Changes — {_entry.Name}"
        : $"Update Scan — {_entry.Name}";

    public string BaseVersionLabel
    {
        get
        {
            if (!IsReadOnly)
                return _entry.LatestVersion is { } v
                    ? $"Comparing against v{v.VersionNumber}  ({v.ScanDate:yyyy-MM-dd})"
                    : string.Empty;

            if (_viewingVersion is null) return string.Empty;
            var idx = _entry.Versions.IndexOf(_viewingVersion);
            var baseVer = idx > 0 ? _entry.Versions[idx - 1] : null;
            return baseVer is not null
                ? $"v{_viewingVersion.VersionNumber} vs v{baseVer.VersionNumber}  |  Scanned {_viewingVersion.ScanDate:yyyy-MM-dd HH:mm}"
                : $"Initial version  |  Scanned {_viewingVersion.ScanDate:yyyy-MM-dd HH:mm}";
        }
    }

    public ObservableCollection<FileRecord> Added { get; }
    public ObservableCollection<FileRecord> Modified { get; }
    public ObservableCollection<FileRecord> Removed { get; }
    public ObservableCollection<FileRecord> Unchanged { get; }

    public string AddedHeader    => $"Added ({Added.Count})";
    public string ModifiedHeader => $"Modified ({Modified.Count})";
    public string RemovedHeader  => $"Removed ({Removed.Count})";
    public string UnchangedHeader=> $"Unchanged ({Unchanged.Count})";

    public string SummaryText =>
        $"+{Added.Count} added   ~{Modified.Count} modified   -{Removed.Count} removed   ={Unchanged.Count} unchanged";

    [ObservableProperty] private string _versionNumber = string.Empty;

    public DiffViewModel(AppEntry entry, IReadOnlyList<FileRecord> files, DiffResult diff,
        MainViewModel main, StorageService storage,
        bool isReadOnly = false, AppVersion? viewingVersion = null)
    {
        _entry = entry;
        _scannedFiles = files;
        _main = main;
        _storage = storage;
        IsReadOnly = isReadOnly;
        _viewingVersion = viewingVersion;

        Added     = new ObservableCollection<FileRecord>(diff.Added);
        Modified  = new ObservableCollection<FileRecord>(diff.Modified);
        Removed   = new ObservableCollection<FileRecord>(diff.Removed);
        Unchanged = new ObservableCollection<FileRecord>(diff.Unchanged);
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

        var prevDebugPaths = _entry.LatestVersion?.Files
            .Where(f => f.IsDebug).Select(f => f.Path).ToHashSet() ?? [];

        var allFiles = _scannedFiles.Select(f =>
        {
            f.IsDebug = prevDebugPaths.Contains(f.Path);
            return f;
        }).ToList();

        _entry.Versions.Add(new AppVersion
        {
            VersionNumber = VersionNumber.Trim(),
            ScanDate = DateTime.Now,
            Files = allFiles,
            HasDiff = true,
            AddedCount = Added.Count,
            ModifiedCount = Modified.Count,
            RemovedCount = Removed.Count,
        });

        _main.NavigateToDetail(_entry);
    }

    // Also used as "Back" in read-only mode
    [RelayCommand]
    private void Cancel() => _main.NavigateToDetail(_entry);
}
