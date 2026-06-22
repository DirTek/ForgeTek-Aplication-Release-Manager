using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class PackageViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildPackageCommand))]
    private string _packageOutputPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PackageFilesLabel))]
    [NotifyPropertyChangedFor(nameof(IsIncrementalSelected))]
    [NotifyPropertyChangedFor(nameof(IsFullSelected))]
    [NotifyCanExecuteChangedFor(nameof(BuildPackageCommand))]
    [NotifyCanExecuteChangedFor(nameof(UploadToFtpCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateUpdateJsonCommand))]
    private bool _isPackagingComplete;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PackageFilesLabel))]
    [NotifyPropertyChangedFor(nameof(IsIncrementalSelected))]
    [NotifyPropertyChangedFor(nameof(IsFullSelected))]
    private PackageType _selectedPackageType = PackageType.Incremental;

    public bool IsIncrementalSelected
    {
        get => SelectedPackageType == PackageType.Incremental;
        set { if (value) SelectedPackageType = PackageType.Incremental; }
    }

    public bool IsFullSelected
    {
        get => SelectedPackageType == PackageType.Full;
        set { if (value) SelectedPackageType = PackageType.Full; }
    }

    public bool IsIncrementalAvailable => _isDiffVersion;

    public string PackageFilesLabel
    {
        get
        {
            var files = SelectedPackageType == PackageType.Incremental ? _incrementalFiles : _fullFiles;
            return SelectedPackageType == PackageType.Incremental
                ? $"{files.Count} file(s) — added and modified only"
                : $"{files.Count} file(s) — all non-debug files";
        }
    }

    [RelayCommand]
    private void BrowsePackageOutput()
    {
        var appSettings = _settings.LoadAppSettings(_entry.Name);
        var ext = string.IsNullOrWhiteSpace(appSettings.PackageExtension)
                      ? "ftu"
                      : appSettings.PackageExtension.TrimStart('.');
        var path = _dialog.SaveFile(
            "Save Package As",
            $"ForgeTek Package (*.{ext})|*.{ext}|All files (*.*)|*.*",
            Path.GetFileName(PackageOutputPath),
            Path.GetDirectoryName(PackageOutputPath));
        if (path is not null) PackageOutputPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanBuildPackage))]
    private async Task BuildPackageAsync()
    {
        IsPackaging = true;
        IsPackagingComplete = false;
        Log.Add(string.Empty);

        var files = SelectedPackageType == PackageType.Incremental ? _incrementalFiles : _fullFiles;

        // A Full has no baseline; an Incremental is cumulative since the last full.
        _version.BaseVersion = SelectedPackageType == PackageType.Incremental ? _baselineVersion : null;

        // Cumulative patches grow as more changes accrue since the baseline; nudge a fresh Full when large.
        if (SelectedPackageType == PackageType.Incremental && _fullFiles.Count > 0
            && _incrementalFiles.Count * 10 >= _fullFiles.Count * 6)
        {
            Log.Add($"⚠  This patch is cumulative since v{_version.BaseVersion} and carries " +
                    $"{_incrementalFiles.Count} of {_fullFiles.Count} files. Consider a Full to reset the baseline.");
        }

        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => Log.Add(msg));
        try
        {
            // Incremental: baseline-relative removed set (genuinely-gone since the last full + files the
            // user marked for removal). Full: self-contained, so only the explicit removals are carried.
            var removedFiles = SelectedPackageType == PackageType.Incremental
                ? _packageRemovedFiles
                : _version.RemovedMarkedFiles.Select(f => f.Path).ToList();
            var sha256 = await _packaging.BuildAsync(
                _entry, _version, files, SelectedPackageType,
                PackageOutputPath, ManifestOutputPath, removedFiles, progress, _cts.Token,
                expectedFiles: _fullFiles);

            Log.Add(string.Empty);
            Log.Add($"Package SHA-256: {sha256}");

            Log.Add(string.Empty);
            await _packaging.VerifyAsync(PackageOutputPath, progress, _cts.Token);

            _version.HasPackage       = true;
            _version.PackagePath      = PackageOutputPath;
            _version.PackageChecksum  = sha256;
            _version.PackageType      = SelectedPackageType;
            _version.Status      = VersionStatus.Packed;
            _version.PipelineStep = PackageStep.Package;
            _storage.Update(_entry);
            IsPackagingComplete = true;
            _main.RefreshSidebar(_entry);
        }
        catch (OperationCanceledException) { Log.Add("Packaging stopped."); }
        catch (Exception ex)              { Log.Add($"✗ {ex.Message}"); }
        finally { _cts.Dispose(); _cts = null; IsPackaging = false; }
    }

    private bool CanBuildPackage()
        => !IsSigning && !IsGenerating && !IsPackaging
           && !string.IsNullOrWhiteSpace(PackageOutputPath)
           && _version.HasManifest;
}
