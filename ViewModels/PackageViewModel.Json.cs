using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.ViewModels;

public partial class PackageViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJsonStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsJsonReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsReadyToAdvance))]
    [NotifyCanExecuteChangedFor(nameof(UploadToFtpCommand))]
    private bool _isJsonComplete;

    public string CatalogLocalPath  => _catalogOutputPath;
    public string CatalogRemotePath => _publish.RemoteTarget(_appSettings, _appKey, null, CatalogFileName);
    public string PackageRemotePath => _publish.RemoteTarget(_appSettings, _appKey, _version.VersionNumber, PackageFileName);
    public string PackageDownloadUrl => _publish.ResolveDownloadUrl(_appSettings, _appKey, _version.VersionNumber, PackageFileName);

    [RelayCommand(CanExecute = nameof(CanGenerateJson))]
    private async Task GenerateUpdateJsonAsync()
    {
        IsGeneratingJson = true;
        IsJsonComplete = false;
        Log.Add(string.Empty);
        Log.Add("Building update catalog…");

        _cts = new CancellationTokenSource();
        try
        {
            string? existingJson = null;

            if (File.Exists(_catalogOutputPath))
            {
                existingJson = await File.ReadAllTextAsync(_catalogOutputPath, _cts.Token);
                Log.Add($"Loaded existing local catalog.");
            }
            else if (HasFtpSettings)
            {
                Log.Add($"Checking {_publish.ProviderName(_appSettings)} for existing catalog…");
                using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                checkCts.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    existingJson = await _publish.TryGetCatalogAsync(_appSettings, _appKey, CatalogFileName, checkCts.Token);
                    Log.Add(existingJson is not null
                        ? "Downloaded existing catalog from the publish target."
                        : "No existing catalog at the publish target — creating new.");
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                {
                    Log.Add("Catalog check timed out — creating new catalog.");
                }
            }
            else
            {
                Log.Add("No existing catalog — creating new.");
            }

            var catalogJson = _catalog.BuildOrMerge(_appKey, _version, PackageDownloadUrl, existingJson);

            Directory.CreateDirectory(Path.GetDirectoryName(_catalogOutputPath) ?? ".");
            await File.WriteAllTextAsync(_catalogOutputPath, catalogJson, _cts.Token);

            Log.Add($"✔  Catalog saved → {_catalogOutputPath}");
            Log.Add(string.Empty);
            Log.Add(catalogJson.Length > 600 ? catalogJson[..600] + "\n…" : catalogJson);
            _version.PipelineStep = PackageStep.Json;
            _storage.Update(_entry);
            IsJsonComplete = true;
            _main.RefreshSidebar(_entry);
        }
        catch (OperationCanceledException) { Log.Add("Catalog generation stopped."); }
        catch (Exception ex)              { Log.Add($"✗  {ex.Message}"); }
        finally { _cts.Dispose(); _cts = null; IsGeneratingJson = false; }
    }

    private bool CanGenerateJson() => !IsGeneratingJson && _version.HasPackage;
}
