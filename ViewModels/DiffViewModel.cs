using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekApplicationReleaseManager.Models;
using ForgeTekApplicationReleaseManager.Services;

namespace ForgeTekApplicationReleaseManager.ViewModels;

public partial class DiffViewModel : ObservableObject
{
    private MainViewModel _main = null!;
    private readonly IStorageService _storage;
    private readonly IDialogService _dialog;
    private readonly ILocalizationService _loc;
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

    public string VersionMismatchText => _loc.Get("Str.DiffVm.VersionMismatch", _detectedExeVersion ?? string.Empty);

    public string ViewTitle => IsReadOnly && _viewingVersion is not null
        ? _loc.Get("Str.DiffVm.ViewTitleReadOnly", _viewingVersion.VersionNumber, _entry.Name)
        : _loc.Get("Str.DiffVm.ViewTitleScan", _entry.Name);

    public string BaseVersionLabel
    {
        get
        {
            if (!IsReadOnly)
                return _entry.LatestVersion is { } v
                    ? _loc.Get("Str.DiffVm.ComparingAgainst", v.VersionNumber, v.ScanDate)
                    : string.Empty;

            if (_viewingVersion is null) return string.Empty;
            var idx = _entry.Versions.IndexOf(_viewingVersion);
            var baseVer = idx > 0 ? _entry.Versions[idx - 1] : null;
            return baseVer is not null
                ? _loc.Get("Str.DiffVm.VsBase", _viewingVersion.VersionNumber, baseVer.VersionNumber, _viewingVersion.ScanDate)
                : _loc.Get("Str.DiffVm.InitialScanned", _viewingVersion.ScanDate);
        }
    }

    public ObservableCollection<FileRecord> Added { get; } = [];
    public ObservableCollection<FileRecord> Modified { get; } = [];
    public ObservableCollection<FileRecord> Removed { get; } = [];
    public ObservableCollection<FileRecord> Excluded { get; } = [];
    public ObservableCollection<FileRecord> Unchanged { get; } = [];

    public string AddedHeader    => _loc.Get("Str.DiffVm.AddedHeader", Added.Count);
    public string ModifiedHeader => _loc.Get("Str.DiffVm.ModifiedHeader", Modified.Count);
    public string RemovedHeader  => _loc.Get("Str.DiffVm.RemovedHeader", Removed.Count);
    public string ExcludedHeader => _loc.Get("Str.DiffVm.ExcludedHeader", Excluded.Count);
    public bool   HasExcluded    => Excluded.Count > 0;
    public string UnchangedHeader=> _loc.Get("Str.DiffVm.UnchangedHeader", Unchanged.Count);

    public string SummaryText =>
        _loc.Get("Str.DiffVm.Summary", Added.Count, Modified.Count, Removed.Count, Unchanged.Count);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVersionMismatch))]
    private string _versionNumber = string.Empty;

    /// <summary>When checked, this version is published to the Beta channel (pre-release).</summary>
    [ObservableProperty] private bool _isBeta;

    public DiffViewModel(IStorageService storage, IDialogService dialog, ILocalizationService loc)
    {
        _storage = storage;
        _dialog = dialog;
        _loc = loc;
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
        foreach (var f in diff.Excluded) Excluded.Add(f);
        foreach (var f in diff.Unchanged) Unchanged.Add(f);
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
        _main.PromptChangelogIfNeeded(_entry, trimmed);
    }

    [RelayCommand]
    private void Cancel() => _main.NavigateToDetail(_entry);
}
