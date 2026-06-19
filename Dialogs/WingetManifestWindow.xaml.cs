using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;
using ForgeTekUpdatePackager.Services.Publishing;

namespace ForgeTekUpdatePackager.Dialogs;

/// <summary>Reviews/edits winget metadata for a generated installer, writes the multi-file manifest,
/// validates it locally, and (optionally, if tooling is present) submits it.</summary>
public partial class WingetManifestWindow : Window
{
    private readonly IWingetManifestService _manifest;
    private readonly IPublishService _publish;
    private readonly ISettingsService _settings;
    private readonly string _appName;
    private readonly string _appKey;
    private readonly GeneratedSetupRecord _record;
    private AppSettings _appSettings;
    // The bundle's own publish target (separate from update settings); used for Suggest/Upload URL.
    private readonly AppSettings? _setupPublish;

    // Our generated installer is the ForgeTek bootstrapper, which accepts these silent switches.
    private const string SilentSwitch = "/VERYSILENT";
    private const string SilentWithProgressSwitch = "/SILENT";

    private string? _generatedFolder;

    public WingetManifestWindow(IWingetManifestService manifest, IPublishService publish,
        ISettingsService settings, string appName, GeneratedSetupRecord record,
        PublishProfile? setupProfile = null)
    {
        InitializeComponent();
        _manifest = manifest;
        _publish = publish;
        _settings = settings;
        _appName = appName;
        _appKey = appName.ToLowerInvariant().Replace(" ", "");
        _record = record;
        _appSettings = settings.LoadAppSettings(appName);
        _setupPublish = setupProfile?.ToAppSettings();

        Prefill();
    }

    private void Prefill()
    {
        var w = _appSettings.Winget;
        var g = _settings.Global;
        var publisher = string.IsNullOrWhiteSpace(g.CompanyName) ? _appName : g.CompanyName;

        SubtitleText.Text = $"{Path.GetFileName(_record.OutputPath)} · v{_record.Version}";

        IdentifierBox.Text = string.IsNullOrWhiteSpace(w.PackageIdentifier)
            ? _manifest.DeriveIdentifier(publisher, _appName)
            : w.PackageIdentifier!;
        PublisherBox.Text = publisher;
        PackageNameBox.Text = _appName;
        VersionBox.Text = _record.Version;
        MonikerBox.Text = w.Moniker ?? _appName.ToLowerInvariant().Replace(" ", "");
        LicenseBox.Text = w.License ?? string.Empty;
        LicenseUrlBox.Text = w.LicenseUrl ?? string.Empty;
        ShortDescriptionBox.Text = w.ShortDescription ?? string.Empty;
        DescriptionBox.Text = w.Description ?? string.Empty;
        PackageUrlBox.Text = w.PackageUrl ?? string.Empty;
        TagsBox.Text = string.Join(", ", w.Tags);
        PublisherUrlBox.Text = g.PublisherUrl ?? string.Empty;
        PublisherSupportUrlBox.Text = g.PublisherSupportUrl ?? string.Empty;
        InstallerUrlBox.Text = w.InstallerUrl ?? string.Empty;
        Sha256Box.Text = ResolveSha256();
        SelectCombo(ArchitectureBox, string.IsNullOrWhiteSpace(w.Architecture) ? "x64" : w.Architecture);
        SelectCombo(InstallerTypeBox, string.IsNullOrWhiteSpace(w.InstallerType) ? "exe" : w.InstallerType);

        OutputFolderBox.Text = Path.Combine(
            Path.GetDirectoryName(_record.OutputPath) ?? AppContext.BaseDirectory, "winget");
    }

    private string ResolveSha256()
    {
        if (!string.IsNullOrWhiteSpace(_record.Sha256)) return _record.Sha256!;
        try
        {
            if (File.Exists(_record.OutputPath))
                return HashUtil.Sha256File(_record.OutputPath);
        }
        catch { }
        return string.Empty;
    }

    private static void SelectCombo(ComboBox combo, string value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        combo.SelectedIndex = 0;
    }

    private static string ComboValue(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;

    private WingetManifestInput? BuildInput()
    {
        var id = IdentifierBox.Text.Trim();
        var version = VersionBox.Text.Trim();
        var installerUrl = InstallerUrlBox.Text.Trim();
        var sha = Sha256Box.Text.Trim();

        if (string.IsNullOrWhiteSpace(id) || !id.Contains('.'))
        {
            Warn("Package identifier must be in \"Publisher.Package\" form.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(version)) { Warn("Version is required."); return null; }
        if (string.IsNullOrWhiteSpace(installerUrl)) { Warn("Installer URL is required (paste one or click Suggest)."); return null; }
        if (string.IsNullOrWhiteSpace(sha)) { Warn("Installer SHA256 is unavailable — the setup file may have moved."); return null; }

        var tags = TagsBox.Text
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return new WingetManifestInput(
            PackageIdentifier: id,
            Version: version,
            Publisher: PublisherBox.Text.Trim(),
            PackageName: PackageNameBox.Text.Trim(),
            InstallerUrl: installerUrl,
            InstallerSha256: sha,
            Architecture: ComboValue(ArchitectureBox),
            InstallerType: ComboValue(InstallerTypeBox),
            Moniker: NullIfBlank(MonikerBox.Text),
            ShortDescription: NullIfBlank(ShortDescriptionBox.Text),
            Description: NullIfBlank(DescriptionBox.Text),
            License: NullIfBlank(LicenseBox.Text),
            LicenseUrl: NullIfBlank(LicenseUrlBox.Text),
            PackageUrl: NullIfBlank(PackageUrlBox.Text),
            PublisherUrl: NullIfBlank(PublisherUrlBox.Text),
            PublisherSupportUrl: NullIfBlank(PublisherSupportUrlBox.Text),
            Tags: tags,
            SilentSwitch: SilentSwitch,
            SilentWithProgressSwitch: SilentWithProgressSwitch);
    }

    private void PersistMetadata()
    {
        var w = _appSettings.Winget;
        w.PackageIdentifier = NullIfBlank(IdentifierBox.Text);
        w.Moniker = NullIfBlank(MonikerBox.Text);
        w.ShortDescription = NullIfBlank(ShortDescriptionBox.Text);
        w.Description = NullIfBlank(DescriptionBox.Text);
        w.License = NullIfBlank(LicenseBox.Text);
        w.LicenseUrl = NullIfBlank(LicenseUrlBox.Text);
        w.PackageUrl = NullIfBlank(PackageUrlBox.Text);
        w.Architecture = ComboValue(ArchitectureBox);
        w.InstallerType = ComboValue(InstallerTypeBox);
        w.InstallerUrl = NullIfBlank(InstallerUrlBox.Text);
        w.Tags = TagsBox.Text
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        _settings.SaveAppSettings(_appName, _appSettings);

        // Publisher URLs are account-wide defaults.
        _settings.Global.PublisherUrl = NullIfBlank(PublisherUrlBox.Text);
        _settings.Global.PublisherSupportUrl = NullIfBlank(PublisherSupportUrlBox.Text);
        _settings.SaveGlobal();
    }

    // ── Actions ───────────────────────────────────────────────────────────

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        var input = BuildInput();
        if (input is null) return;

        try
        {
            var root = string.IsNullOrWhiteSpace(OutputFolderBox.Text)
                ? Path.Combine(Path.GetDirectoryName(_record.OutputPath) ?? ".", "winget")
                : OutputFolderBox.Text.Trim();

            _generatedFolder = _manifest.Write(input, root);
            PersistMetadata();
            StatusText.Text = $"Generated · {_generatedFolder}";
            Log($"Wrote 3 manifest files to:\n{_generatedFolder}");
            try { System.Diagnostics.Process.Start("explorer.exe", $"\"{_generatedFolder}\""); } catch { }
        }
        catch (Exception ex)
        {
            Warn($"Could not write the manifest: {ex.Message}");
        }
    }

    private async void Validate_Click(object sender, RoutedEventArgs e)
    {
        if (_generatedFolder is null || !Directory.Exists(_generatedFolder))
        {
            Warn("Generate the manifest first.");
            return;
        }
        StatusText.Text = "Validating…";
        Log("> winget validate --manifest " + _generatedFolder);
        try
        {
            var progress = new Progress<string>(line => Append(line));
            var result = await ProcessRunner.RunAsync("winget",
                $"validate --manifest \"{_generatedFolder}\"", progress: progress);
            StatusText.Text = result.Succeeded ? "✓ Manifest valid" : "Validation reported issues";
        }
        catch (ToolNotFoundException)
        {
            StatusText.Text = "winget not found";
            Log("winget is not installed. Install \"App Installer\" from the Microsoft Store to validate locally.");
        }
        catch (Exception ex)
        {
            Warn($"Validation failed: {ex.Message}");
        }
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_generatedFolder is null || !Directory.Exists(_generatedFolder))
        {
            Warn("Generate the manifest first.");
            return;
        }
        StatusText.Text = "Submitting…";
        Log("> wingetcreate submit \"" + _generatedFolder + "\"");
        try
        {
            var progress = new Progress<string>(line => Append(line));
            var result = await ProcessRunner.RunAsync("wingetcreate",
                $"submit \"{_generatedFolder}\"", progress: progress);
            StatusText.Text = result.Succeeded ? "✓ Submitted" : "Submit reported issues";
        }
        catch (ToolNotFoundException)
        {
            StatusText.Text = "wingetcreate not found";
            var choice = MessageBox.Show(this,
                "wingetcreate isn't installed. Install it now with winget?",
                "Install wingetcreate", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Yes)
                await InstallWingetCreateAsync();
            else
            {
                Log("To submit later, install wingetcreate (winget install Microsoft.WingetCreate), " +
                    "or open https://github.com/microsoft/winget-pkgs to submit the files manually.");
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    { FileName = "https://github.com/microsoft/winget-pkgs", UseShellExecute = true }); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Warn($"Submit failed: {ex.Message}");
        }
    }

    private async Task InstallWingetCreateAsync()
    {
        StatusText.Text = "Installing wingetcreate…";
        Log("> winget install --id Microsoft.WingetCreate -e --source winget");
        try
        {
            var progress = new Progress<string>(Append);
            var result = await ProcessRunner.RunAsync("winget",
                "install --id Microsoft.WingetCreate -e --source winget " +
                "--accept-source-agreements --accept-package-agreements", progress: progress);
            StatusText.Text = result.Succeeded
                ? "✓ wingetcreate installed — click Submit again."
                : "Install reported issues — see log.";
        }
        catch (ToolNotFoundException)
        {
            StatusText.Text = "winget not found";
            Log("winget (App Installer) isn't available, so wingetcreate can't be installed automatically. " +
                "Install \"App Installer\" from the Microsoft Store first.");
        }
        catch (Exception ex)
        {
            Warn($"Install failed: {ex.Message}");
        }
    }

    private void SuggestUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_setupPublish is null)
        {
            Warn("No setup-publish target is configured for this bundle. Use \"Publish settings…\" (next to Generate), or paste the URL manually.");
            return;
        }
        try
        {
            var fileName = Path.GetFileName(_record.OutputPath);
            var url = _publish.ResolveDownloadUrl(_setupPublish, _appKey, VersionBox.Text.Trim(), fileName);
            InstallerUrlBox.Text = url;
            StatusText.Text = $"Suggested URL from {_publish.ProviderName(_setupPublish)} — verify the installer is uploaded there.";
        }
        catch (Exception ex)
        {
            Warn($"Could not resolve a URL: {ex.Message}");
        }
    }

    private async void UploadUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_setupPublish is null)
        {
            Warn("No setup-publish target is configured for this bundle. Use \"Publish settings…\" (next to Generate), or paste the URL manually.");
            return;
        }
        if (!File.Exists(_record.OutputPath))
        {
            Warn($"The setup file is missing:\n{_record.OutputPath}");
            return;
        }

        UploadUrlBtn.IsEnabled = false;
        StatusText.Text = "Uploading installer…";
        try
        {
            var fileName = Path.GetFileName(_record.OutputPath);
            var progress = new Progress<string>(Append);
            var url = await _publish.UploadArtifactAsync(_setupPublish, _appKey, VersionBox.Text.Trim(),
                _record.OutputPath, fileName, progress);
            InstallerUrlBox.Text = url;
            StatusText.Text = $"✓ Uploaded to {_publish.ProviderName(_setupPublish)}";
        }
        catch (Exception ex)
        {
            Warn($"Upload failed: {ex.Message}");
        }
        finally
        {
            UploadUrlBtn.IsEnabled = true;
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select output folder for the manifest" };
        if (dlg.ShowDialog() == true)
            OutputFolderBox.Text = dlg.FolderName;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Warn(string message)
    {
        StatusText.Text = message;
        Log(message);
    }

    private void Log(string text)
    {
        LogPanel.Visibility = Visibility.Visible;
        LogText.Text = text;
    }

    private void Append(string line)
    {
        LogPanel.Visibility = Visibility.Visible;
        LogText.Text = string.IsNullOrEmpty(LogText.Text) ? line : LogText.Text + "\n" + line;
    }
}
