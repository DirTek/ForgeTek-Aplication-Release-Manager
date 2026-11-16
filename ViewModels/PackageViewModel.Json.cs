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
            var localJson = File.Exists(_catalogOutputPath)
                ? await File.ReadAllTextAsync(_catalogOutputPath, _cts.Token)
                : null;

            if (HasFtpSettings)
            {
                // The server's catalog is what clients read, so it is the one to merge into. The local file is
                // only a copy of what was last uploaded from this machine: it misses retractions and anything
                // published from another machine or an older workspace, and uploading a merge of it would
                // overwrite those. If the server can't be read, stop rather than guess.
                var provider = _publish.ProviderName(_appSettings);
                Log.Add($"Reading the current catalog from {provider}…");
                using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                checkCts.CancelAfter(TimeSpan.FromSeconds(60));
                try
                {
                    var token = checkCts.Token;
                    existingJson = await Task.Run(
                        () => _publish.TryGetCatalogAsync(_appSettings, _appKey, CatalogFileName, token), token);
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                {
                    throw new InvalidOperationException(
                        $"Timed out reading the current catalog from {provider}. Nothing was changed — try again.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        $"Couldn't read the current catalog from {provider}: {ex.Message} Nothing was changed — " +
                        "try again once the server is reachable.", ex);
                }

                if (existingJson is null)
                    Log.Add(localJson is null
                        ? "No catalog on the server yet — starting a new one."
                        : "No catalog on the server — starting a new one (the local copy is ignored; it isn't what clients see).");
                else
                    Log.Add(localJson is not null && localJson != existingJson
                        ? "Loaded the live catalog from the server (the local copy was out of date and will be replaced)."
                        : "Loaded the live catalog from the server.");
            }
            else if (localJson is not null)
            {
                existingJson = localJson;
                Log.Add("No publish target configured — merging into the local catalog copy.");
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
