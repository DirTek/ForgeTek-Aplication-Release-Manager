using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.ViewModels;

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
        Log.Add(string.Empty);
        Log.Add($"Uploading to {_ftpHost} …");
        _log.Write("FTP", $"=== Upload session start — {_ftpHost}:{_ftpPort} ===");

        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => { Log.Add(msg); _log.Write("FTP", msg); });
        try
        {
            if (_version.PackagePath is null)
                throw new InvalidOperationException("Package path is null — build the package first.");

            var uploads = new (string Local, string Remote)[]
            {
                (_version.PackagePath, PackageRemotePath),
                (_catalogOutputPath,    CatalogRemotePath),
            };

            var host = _ftpHost ?? throw new InvalidOperationException("FTP host is not configured.");
            await Task.Run(() => _ftpService.UploadFilesAsync(
                uploads.Select(u => (u.Local, u.Remote)),
                host, _ftpPort, _ftpUsername, _ftpPassword, progress, _cts.Token));

            Log.Add(string.Empty);
            Log.Add("✔  All files uploaded.");

            _version.FtpPackageRemotePath = PackageRemotePath;
            _version.FtpCatalogRemotePath = CatalogRemotePath;
            _version.FtpHost              = _ftpHost;
            _version.FtpPort              = _ftpPort;
            _version.FtpUsername          = _ftpUsername;
            _version.FtpPassword          = _ftpPassword;
            _version.PipelineStep         = PackageStep.Ftp;
            _storage.Update(_entry);

            IsFtpComplete = true;
            _main.RefreshSidebar(_entry);
        }
        catch (OperationCanceledException) { Log.Add("Upload stopped."); }
        catch (Exception ex)              { Log.Add($"✗  {ex.Message}"); }
        finally { _cts.Dispose(); _cts = null; IsUploading = false; }
    }

    private bool CanUploadToFtp()
        => !IsUploading && HasFtpSettings
           && _version.HasPackage
           && IsJsonComplete
           && File.Exists(_catalogOutputPath);
}
