using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class DiffViewModel : ObservableObject
{
    private MainViewModel _main = null!;
    private readonly IStorageService _storage;
    private readonly IDialogService _dialog;
    private AppEntry _entry = null!;
    private IReadOnlyList<FileRecord> _scannedFiles = [];
    private AppVersion? _viewingVersion;

    private string? _detectedExeVersion;

    public bool IsReadOnly { get; private set; }
    public bool IsApprovalMode => !IsReadOnly;

    public string AppName => _entry.Name;

    public bool HasVersionMismatch =>
        _detectedExeVersion is not null &&
        !string.Equals(VersionNumber.Trim(), _detectedExeVersion, StringComparison.OrdinalIgnoreCase);

    public string VersionMismatchText => $"EXE file version is v{_detectedExeVersion}";

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

    public ObservableCollection<FileRecord> Added { get; } = [];
    public ObservableCollection<FileRecord> Modified { get; } = [];
    public ObservableCollection<FileRecord> Removed { get; } = [];
    public ObservableCollection<FileRecord> Unchanged { get; } = [];

    public string AddedHeader    => $"Added ({Added.Count})";
    public string ModifiedHeader => $"Modified ({Modified.Count})";
    public string RemovedHeader  => $"Removed ({Removed.Count})";
    public string UnchangedHeader=> $"Unchanged ({Unchanged.Count})";

    public string SummaryText =>
        $"+{Added.Count} added   ~{Modified.Count} modified   -{Removed.Count} removed   ={Unchanged.Count} unchanged";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVersionMismatch))]
    private string _versionNumber = string.Empty;

    /// <summary>When checked, this version is published to the Beta channel (pre-release).</summary>
    [ObservableProperty] private bool _isBeta;

    public DiffViewModel(IStorageService storage, IDialogService dialog)
    {
        _storage = storage;
        _dialog = dialog;
    }

    public void Initialize(AppEntry entry, IReadOnlyList<FileRecord> files, DiffResult diff,
        MainViewModel main, bool isReadOnly = false, AppVersion? viewingVersion = null, string? detectedVersion = null)
    {
        _entry = entry;
        _scannedFiles = files;
        _main = main;
        IsReadOnly = isReadOnly;
        _viewingVersion = viewingVersion;
        _detectedExeVersion = detectedVersion;
        VersionNumber = detectedVersion ?? string.Empty;

        foreach (var f in diff.Added) Added.Add(f);
        foreach (var f in diff.Modified) Modified.Add(f);
        foreach (var f in diff.Removed) Removed.Add(f);
        foreach (var f in diff.Unchanged) Unchanged.Add(f);
    }

    [RelayCommand]
    private void Approve()
    {
        if (string.IsNullOrWhiteSpace(VersionNumber))
        {
            _dialog.Alert("Missing Version", "Please enter a version number.");
            return;
        }

        var trimmed = VersionNumber.Trim();

        if (_entry.Versions.Any(v => v.VersionNumber == trimmed))
        {
            if (!_dialog.Confirm("Duplicate Version",
                    $"Version {trimmed} has already been scanned for this app. Saving it again may cause update loops.\n\nSave anyway?",
                    "Save Anyway")) return;
        }

        if (_detectedExeVersion is not null &&
            Version.TryParse(trimmed, out var typedVer) &&
            Version.TryParse(_detectedExeVersion, out var exeVer) &&
            typedVer > exeVer)
        {
            if (!_dialog.Confirm("Update Loop Risk",
                    $"You are packaging version {trimmed}, but the EXE reports v{_detectedExeVersion}.\n\n" +
                    $"Clients will see {trimmed} in the catalog, check their installed EXE ({_detectedExeVersion}), " +
                    $"download the update — and then loop forever because the EXE version never changes.\n\nSave anyway?",
                    "Save Anyway")) return;
        }

        var prevDebugPaths = _entry.LatestVersion?.Files
            .Where(f => f.IsDebug).Select(f => f.Path).ToHashSet() ?? [];

        var allFiles = _scannedFiles.Select(f =>
        {
            f.IsDebug = f.IsDebug || prevDebugPaths.Contains(f.Path);
            return f;
        }).ToList();

        var version = new AppVersion
        {
            VersionNumber = trimmed,
            ScanDate = DateTime.Now,
            Channel = IsBeta ? UpdateChannel.Beta : UpdateChannel.Stable,
            Files = allFiles,
            HasDiff = true,
            AddedCount = Added.Count,
            ModifiedCount = Modified.Count,
            RemovedCount = Removed.Count,
            RemovedFiles = Removed.Select(f => f.Path).ToList(),
        };
        _entry.Versions.Add(version);
        _main.NavigateToDetail(_entry);
    }

    [RelayCommand]
    private void Cancel() => _main.NavigateToDetail(_entry);
}
