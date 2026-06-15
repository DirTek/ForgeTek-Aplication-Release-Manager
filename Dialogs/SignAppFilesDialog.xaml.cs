using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;
using ForgeTekUpdatePackager.ViewModels;

namespace ForgeTekUpdatePackager.Dialogs;

/// <summary>
/// Lets the user choose which of an app's files to code-sign before it's packed into a setup —
/// mirroring the per-file sign step used when publishing a version. Signs the on-disk files with
/// the configured global certificate and refreshes the stored checksums.
/// </summary>
public partial class SignAppFilesDialog : Window
{
    private readonly AppEntry _app;
    private readonly ISigningService _signing;
    private readonly ISettingsService _settings;
    private readonly IStorageService _storage;
    private readonly ObservableCollection<SignableFileItem> _files = [];

    /// <param name="scope">A short label for the file set (e.g. "v1.2.0"); shown in the title.</param>
    /// <param name="files">Candidate files (debug/non-signable are filtered out automatically).</param>
    public SignAppFilesDialog(AppEntry app, string scope, IEnumerable<FileRecord> files,
        ISigningService signing, ISettingsService settings, IStorageService storage)
    {
        InitializeComponent();
        _app = app;
        _signing = signing;
        _settings = settings;
        _storage = storage;

        TitleText.Text = string.IsNullOrWhiteSpace(scope)
            ? $"Sign Files — {app.Name}"
            : $"Sign Files — {app.Name} ({scope})";

        var g = settings.Global;
        CertText.Text = g.UseStoreCert && !string.IsNullOrWhiteSpace(g.StoreCertThumbprint)
            ? $"Certificate: Windows store ({g.StoreCertThumbprint[..Math.Min(8, g.StoreCertThumbprint.Length)]}…)"
            : !string.IsNullOrWhiteSpace(g.GlobalCertPath)
                ? $"Certificate: {Path.GetFileName(g.GlobalCertPath)}"
                : "⚠ No signing certificate configured — set one in Settings → Global Options.";

        foreach (var sf in signing.GetSignableFiles(files, app.FolderPath))
            _files.Add(new SignableFileItem(sf));

        FileList.ItemsSource = _files;
        CountText.Text = _files.Count == 0
            ? "No signable files (.exe, .dll, .sys, .ocx, .msi, .cab, .cat)."
            : $"{_files.Count} signable file(s)";
        SignBtn.IsEnabled = _files.Count > 0;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _files) f.IsSelected = true;
    }

    private void DeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _files) f.IsSelected = false;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Sign_Click(object sender, RoutedEventArgs e)
    {
        var selected = _files.Where(f => f.IsSelected).Select(f => f.FullPath).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "Select at least one file to sign.";
            return;
        }

        var signTool = _signing.FindSignTool();
        if (signTool is null)
        {
            StatusText.Text = "✗ signtool.exe not found — install the Windows 10/11 SDK or add it to PATH.";
            return;
        }

        var g = _settings.Global;
        var haveCert = g.UseStoreCert
            ? !string.IsNullOrWhiteSpace(g.StoreCertThumbprint)
            : !string.IsNullOrWhiteSpace(g.GlobalCertPath);
        if (!haveCert)
        {
            StatusText.Text = "✗ No signing certificate configured — set one in Settings → Global Options.";
            return;
        }

        SignBtn.IsEnabled = false;
        StatusText.Text = $"Signing {selected.Count} file(s)…";
        var progress = new Progress<string>(m => StatusText.Text = m);

        try
        {
            await _signing.SignFilesAsync(signTool, selected,
                g.UseStoreCert ? null : g.GlobalCertPath,
                g.UseStoreCert ? null : g.GlobalCertPassword,
                g.UseStoreCert ? g.StoreCertThumbprint : null,
                progress);

            // Signing changed the files on disk — refresh the stored checksums for every version
            // record that points at a signed file, then persist.
            foreach (var fullPath in selected)
            {
                var rel = Path.GetRelativePath(_app.FolderPath, fullPath);
                var checksum = ScannerService.ComputeChecksum(fullPath);
                foreach (var version in _app.Versions)
                    foreach (var record in version.Files.Where(f => f.Path == rel))
                        record.Checksum = checksum;
            }
            _storage.Update(_app);

            StatusText.Text = $"✔ Signed {selected.Count} file(s) and updated checksums.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"✗ {ex.Message}";
        }
        finally
        {
            SignBtn.IsEnabled = true;
        }
    }
}
