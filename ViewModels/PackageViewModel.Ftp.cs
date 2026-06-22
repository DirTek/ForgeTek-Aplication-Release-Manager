using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.ViewModels;

public partial class PackageViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFtpStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsFtpReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsReadyToAdvance))]
    private bool _isFtpComplete;

    [RelayCommand(CanExecute = nameof(CanUploadToFtp))]
    private async Task UploadToFtpAsync()
    {
        IsUploading = true;
        IsFtpComplete = false;
        var providerName = _publish.ProviderName(_appSettings);
        Log.Add(string.Empty);
        Log.Add($"Publishing via {providerName} …");
        _log.Write("Publish", $"=== Publish session start — {providerName} ===");

        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => { Log.Add(msg); _log.Write("Publish", msg); });
        try
        {
            if (_version.PackagePath is null)
                throw new InvalidOperationException("Package path is null — build the package first.");

            // Defense in depth: never publish an unapproved release (the button is also disabled).
            if (!IsApproved)
            {
                Log.Add($"✗  Blocked: {ApprovalHint}");
                return;
            }

            // Run the transport off the UI thread (like the dashboard's connection check) so its async
            // work can't deadlock against the WPF dispatcher. Progress callbacks still marshal to the UI.
            var token = _cts.Token;
            await Task.Run(() => _publish.UploadReleaseAsync(_appSettings, _appKey, _version.VersionNumber,
                _version.PackagePath!, PackageFileName,
                _catalogOutputPath, CatalogFileName,
                progress, token), token);

            Log.Add(string.Empty);
            Log.Add("✔  All files published.");

            _version.PublishProvider      = _appSettings.PublishProvider;
            _version.FtpPackageRemotePath = PackageRemotePath;
            _version.FtpCatalogRemotePath = CatalogRemotePath;
            _version.PipelineStep         = PackageStep.Ftp;
            _storage.Update(_entry);

            IsFtpComplete = true;
            _main.RefreshSidebar(_entry);
        }
        catch (OperationCanceledException) { Log.Add("Publish stopped."); }
        catch (Exception ex)              { Log.Add($"✗  {ex.Message}"); }
        finally { _cts.Dispose(); _cts = null; IsUploading = false; }
    }

    private bool CanUploadToFtp()
        => !IsUploading && HasFtpSettings
           && _version.HasPackage
           && IsJsonComplete
           && IsApproved
           && File.Exists(_catalogOutputPath);
}
