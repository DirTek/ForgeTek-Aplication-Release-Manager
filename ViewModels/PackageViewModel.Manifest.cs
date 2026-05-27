using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.ViewModels;

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
            var json = await _manifest.GenerateAsync(_entry, _version, _incrementalFiles, _version.RemovedFiles, progress, _cts.Token);
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestOutputPath) ?? ".");
            await File.WriteAllTextAsync(ManifestOutputPath, json, _cts.Token);
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

    private bool CanGenerateManifest()
        => !IsSigning && !IsGenerating && !IsPackaging
           && !string.IsNullOrWhiteSpace(ManifestOutputPath);
}
