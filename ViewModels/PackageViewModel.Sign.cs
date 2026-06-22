using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekApplicationReleaseManager.Helpers;
using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.ViewModels;

public partial class PackageViewModel
{
    public string FilesLabel
    {
        get
        {
            var count = SignableFiles.Count;
            if (count == 0) return "No signable files found (.exe, .dll, .sys, .ocx, .msi, .cab, .cat)";
            var scope = _isDiffVersion ? " — new and modified only" : string.Empty;
            return count == 1 ? $"1 file{scope}" : $"{count} files{scope}";
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignCommand))]
    private string _pfxPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignCommand))]
    private string _storeThumbprint = string.Empty;

    public ObservableCollection<StoreCertInfo> AvailableStoreCerts { get; } = [];

    [ObservableProperty] private bool _useStoreCert;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignStepIdle))]
    [NotifyPropertyChangedFor(nameof(IsSignReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsReadyToAdvance))]
    [NotifyPropertyChangedFor(nameof(IsSkipVisible))]
    private bool _isSigningComplete;

    public string PfxPassword { get; set; } = string.Empty;
    public ObservableCollection<SignableFileItem> SignableFiles { get; } = [];

    [RelayCommand]
    private void SelectAll()   { foreach (var f in SignableFiles) f.IsSelected = true; }

    [RelayCommand]
    private void DeselectAll() { foreach (var f in SignableFiles) f.IsSelected = false; }

    [RelayCommand]
    private void BrowsePfx()
    {
        var path = _dialog.OpenFile("Select PFX Certificate", "PFX Certificate (*.pfx)|*.pfx|All files (*.*)|*.*");
        if (path is not null) PfxPath = path;
    }

    [RelayCommand]
    private void BrowseStoreCert()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        try
        {
            store.Open(OpenFlags.ReadOnly);
            AvailableStoreCerts.Clear();
            foreach (var cert in store.Certificates)
            {
                using (cert)
                    AvailableStoreCerts.Add(StoreCertInfo.FromX509(cert));
            }
            store.Close();
        }
        catch { AvailableStoreCerts.Clear(); return; }

        if (AvailableStoreCerts.Count > 0)
        {
            UseStoreCert = true;
            StoreThumbprint = AvailableStoreCerts[0].Thumbprint;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSign))]
    private async Task SignAsync()
    {
        IsSigning = true;
        IsSigningComplete = false;
        Log.Clear();

        var selected = SignableFiles.Where(f => f.IsSelected).Select(f => f.FullPath).ToList();
        Log.Add($"Signing {selected.Count} of {SignableFiles.Count} file(s) with {(UseStoreCert ? StoreThumbprint : Path.GetFileName(PfxPath))}…");

        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => Log.Add(msg));
        try
        {
            if (_signToolPath is null)
                throw new InvalidOperationException("signtool.exe not found. Install Windows SDK or add it to PATH.");

            await _signing.SignFilesAsync(_signToolPath, selected,
                UseStoreCert ? null : PfxPath,
                UseStoreCert ? null : PfxPassword,
                UseStoreCert ? StoreThumbprint : null,
                progress, _cts.Token);

            RecomputeSignedChecksums(selected);

            _version.Status = VersionStatus.Signed;
            _version.PipelineStep = PackageStep.Sign;
            _storage.Update(_entry);
            Log.Add(string.Empty);
            Log.Add("✔  Signing complete.");
            IsSigningComplete = true;
            _main.RefreshSidebar(_entry);
        }
        catch (OperationCanceledException) { Log.Add("Signing stopped."); }
        catch (Exception ex)              { Log.Add($"✗ {ex.Message}"); }
        finally { _cts.Dispose(); _cts = null; IsSigning = false; }
    }

    private void RecomputeSignedChecksums(IReadOnlyList<string> signedPaths)
    {
        foreach (var path in signedPaths)
        {
            var relPath = Path.GetRelativePath(_entry.FolderPath, path);
            var newChecksum = _scanner.ComputeChecksum(path);

            var record = _version.Files.FirstOrDefault(f => f.Path == relPath);
            if (record is not null)
                record.Checksum = newChecksum;

            var fullFile = _fullFiles.FirstOrDefault(f => f.Path == relPath);
            if (fullFile is not null)
                fullFile.Checksum = newChecksum;

            var incFile = _incrementalFiles.FirstOrDefault(f => f.Path == relPath);
            if (incFile is not null)
                incFile.Checksum = newChecksum;
        }
    }

    private bool CanSign()
        => !IsSigning && !IsGenerating && !IsPackaging
           && SignableFiles.Any(f => f.IsSelected)
           && (UseStoreCert ? !string.IsNullOrWhiteSpace(StoreThumbprint) : !string.IsNullOrWhiteSpace(PfxPath))
           && _signToolPath is not null;
}
