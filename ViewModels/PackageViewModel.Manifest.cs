using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.ViewModels;

public partial class PackageViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateManifestCommand))]
    private string _manifestOutputPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManifestStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsManifestReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsReadyToAdvance))]
    private bool _isManifestComplete;

    public bool HasManifest => _version.HasManifest;

    [RelayCommand]
    private void BrowseManifestOutput()
    {
        var path = _dialog.SaveFile("Save Manifest As", "JSON (*.json)|*.json|All files (*.*)|*.*", "manifest.json");
        if (path is not null) ManifestOutputPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanGenerateManifest))]
    private async Task GenerateManifestAsync()
    {
        IsGenerating = true;
        IsManifestComplete = false;
        Log.Add(string.Empty);
        Log.Add($"Building manifest for v{_version.VersionNumber}…");

        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => Log.Add(msg));
        try
        {
            await WriteManifestAsync(progress, _cts.Token);
            Log.Add(string.Empty);
            Log.Add($"✔  Manifest saved → {ManifestOutputPath}");
            _version.HasManifest = true;
            _version.PipelineStep = PackageStep.Manifest;
            _storage.Update(_entry);
            OnPropertyChanged(nameof(HasManifest));
            BuildPackageCommand.NotifyCanExecuteChanged();
            IsManifestComplete = true;
            _main.RefreshSidebar(_entry);
        }
        catch (OperationCanceledException) { Log.Add("Manifest generation stopped."); }
        catch (Exception ex)              { Log.Add($"✗ {ex.Message}"); }
        finally { _cts.Dispose(); _cts = null; IsGenerating = false; }
    }

    /// <summary>Writes the manifest for the currently selected package type: the full expected state (every
    /// file a client must have afterwards + hash) and the same RemovedFiles/baseline as the package header.
    /// Also run at packaging time, because the package type can change after the Manifest step.</summary>
    private async Task WriteManifestAsync(IProgress<string> progress, CancellationToken ct)
    {
        // A Full has no baseline; an Incremental is cumulative since the last full.
        _version.BaseVersion = SelectedPackageType == PackageType.Incremental ? _baselineVersion : null;

        var json = await _manifest.GenerateAsync(_entry, _version, _expectedFiles, CurrentRemovedFiles(), progress, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestOutputPath) ?? ".");
        await File.WriteAllTextAsync(ManifestOutputPath, json, ct);
    }

    private bool CanGenerateManifest()
        => !IsSigning && !IsGenerating && !IsPackaging
           && !string.IsNullOrWhiteSpace(ManifestOutputPath);
}
