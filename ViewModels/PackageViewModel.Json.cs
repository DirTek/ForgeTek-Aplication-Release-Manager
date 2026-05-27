using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class PackageViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJsonStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsJsonReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsReadyToAdvance))]
    [NotifyCanExecuteChangedFor(nameof(UploadToFtpCommand))]
    private bool _isJsonComplete;

    public string CatalogLocalPath  => _catalogOutputPath;
    public string CatalogRemotePath => BuildRemotePath(_ftpRemotePath, _appKey, null, $"{_appKey}.json");
    public string PackageRemotePath => BuildRemotePath(_ftpRemotePath, _appKey, _version.VersionNumber, Path.GetFileName(PackageOutputPath));
    public string PackageDownloadUrl => BuildDownloadUrl(_baseDownloadUrl, _appKey, _version.VersionNumber, Path.GetFileName(PackageOutputPath));

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
                Log.Add("Checking FTP for existing catalog…");
                using var ftpCheckCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                ftpCheckCts.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    existingJson = await Task.Run(() => _ftpService.TryDownloadStringAsync(
                        CatalogRemotePath, _ftpHost ?? throw new InvalidOperationException("FTP host not configured."), _ftpPort, _ftpUsername, _ftpPassword, ftpCheckCts.Token));
                    Log.Add(existingJson is not null
                        ? "Downloaded existing catalog from FTP."
                        : "No existing catalog on FTP — creating new.");
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                {
                    Log.Add("FTP check timed out — creating new catalog.");
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
