using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ForgeTekApplicationReleaseManager.Models;
using ForgeTekApplicationReleaseManager.Services;
using ForgeTekApplicationReleaseManager.Services.Publishing;

namespace ForgeTekApplicationReleaseManager.Dialogs;

/// <summary>Edits a setup bundle's own publish target (separate from the apps' update settings),
/// with a "Copy from app" helper and a connection test.</summary>
public partial class SetupPublishSettingsWindow : Window
{
    private readonly IPublishService _publish;
    private readonly IStorageService _storage;
    private readonly ISettingsService _settings;
    private readonly ISetupStorageService _setupStorage;
    private readonly SetupBundle _bundle;

    public SetupPublishSettingsWindow(IPublishService publish, IStorageService storage,
        ISettingsService settings, ISetupStorageService setupStorage, SetupBundle bundle)
    {
        InitializeComponent();
        _publish = publish;
        _storage = storage;
        _settings = settings;
        _setupStorage = setupStorage;
        _bundle = bundle;

        SubtitleText.Text = $"{bundle.Name} — installer published separately from your apps' updates.";
        CopyFromAppBox.ItemsSource = _storage.GetAll().OrderBy(a => a.Name).ToList();

        Load(bundle.PublishProfile ?? new PublishProfile());
    }

    private void Load(PublishProfile p)
    {
        SelectProvider(string.IsNullOrWhiteSpace(p.PublishProvider) ? "Ftp" : p.PublishProvider!);

        FtpHostBox.Text = p.FtpHost ?? "";
        FtpUserBox.Text = p.FtpUsername ?? "";
        FtpPassBox.Password = p.FtpPassword ?? "";
        FtpPathBox.Text = p.FtpRemotePath ?? "";
        FtpPortBox.Text = p.FtpPort.ToString();
        FtpBaseUrlBox.Text = p.BaseDownloadUrl ?? "";

        SftpHostBox.Text = p.SftpHost ?? "";
        SftpUserBox.Text = p.SftpUsername ?? "";
        SftpPassBox.Password = p.SftpPassword ?? "";
        SftpPathBox.Text = p.SftpRemotePath ?? "";
        SftpPortBox.Text = p.SftpPort.ToString();
        SftpBaseUrlBox.Text = p.SftpBaseDownloadUrl ?? "";

        S3BucketBox.Text = p.S3Bucket ?? "";
        S3RegionBox.Text = p.S3Region ?? "";
        S3EndpointBox.Text = p.S3Endpoint ?? "";
        S3AccessKeyBox.Text = p.S3AccessKey ?? "";
        S3SecretBox.Password = p.S3SecretKey ?? "";
        S3PrefixBox.Text = p.S3Prefix ?? "";
        S3PublicBaseUrlBox.Text = p.S3PublicBaseUrl ?? "";

        GhRepoBox.Text = p.GitHubRepo ?? "";
        GhTokenBox.Password = p.GitHubToken ?? "";
        GhReleaseTagBox.Text = p.GitHubReleaseTag ?? "";
    }

    private PublishProfile ReadProfile()
    {
        int.TryParse(FtpPortBox.Text, out var ftpPort);
        int.TryParse(SftpPortBox.Text, out var sftpPort);
        return new PublishProfile
        {
            PublishProvider = SelectedProvider(),
            FtpHost = Nz(FtpHostBox.Text), FtpPort = ftpPort == 0 ? 21 : ftpPort,
            FtpUsername = Nz(FtpUserBox.Text), FtpPassword = Nz(FtpPassBox.Password),
            FtpRemotePath = Nz(FtpPathBox.Text), BaseDownloadUrl = Nz(FtpBaseUrlBox.Text),
            SftpHost = Nz(SftpHostBox.Text), SftpPort = sftpPort == 0 ? 22 : sftpPort,
            SftpUsername = Nz(SftpUserBox.Text), SftpPassword = Nz(SftpPassBox.Password),
            SftpRemotePath = Nz(SftpPathBox.Text), SftpBaseDownloadUrl = Nz(SftpBaseUrlBox.Text),
            S3Bucket = Nz(S3BucketBox.Text), S3Region = Nz(S3RegionBox.Text), S3Endpoint = Nz(S3EndpointBox.Text),
            S3AccessKey = Nz(S3AccessKeyBox.Text), S3SecretKey = Nz(S3SecretBox.Password),
            S3Prefix = Nz(S3PrefixBox.Text), S3PublicBaseUrl = Nz(S3PublicBaseUrlBox.Text),
            GitHubRepo = Nz(GhRepoBox.Text), GitHubToken = Nz(GhTokenBox.Password),
            GitHubReleaseTag = Nz(GhReleaseTagBox.Text),
        };
    }

    // ── Actions ───────────────────────────────────────────────────────────

    private void CopyFromApp_Click(object sender, RoutedEventArgs e)
    {
        if (CopyFromAppBox.SelectedItem is not AppEntry app)
        {
            StatusText.Text = "Pick an app to copy from.";
            return;
        }
        var s = _settings.LoadAppSettings(app.Name);
        Load(PublishProfile.FromAppSettings(s));
        StatusText.Text = $"Copied publish settings from {app.Name}.";
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var profile = ReadProfile();
        if (!profile.IsConfigured())
        {
            StatusText.Text = "Fill in the selected provider's fields first.";
            return;
        }
        TestBtn.IsEnabled = false;
        StatusText.Text = "Testing…";
        try
        {
            var msg = await _publish.TestAsync(profile.ToAppSettings());
            StatusText.Text = msg;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Test failed: {ex.Message}";
        }
        finally
        {
            TestBtn.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _bundle.PublishProfile = ReadProfile();
        try
        {
            _setupStorage.Save(_bundle);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not save: {ex.Message}";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    // ── Provider panel toggling ─────────────────────────────────────────────

    private void Provider_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePanels();

    private void UpdatePanels()
    {
        var provider = SelectedProvider();
        FtpPanel.Visibility = provider == "Ftp" ? Visibility.Visible : Visibility.Collapsed;
        SftpPanel.Visibility = provider == "Sftp" ? Visibility.Visible : Visibility.Collapsed;
        S3Panel.Visibility = provider == "S3" ? Visibility.Visible : Visibility.Collapsed;
        GitHubPanel.Visibility = provider == "GitHubReleases" ? Visibility.Visible : Visibility.Collapsed;
    }

    private string SelectedProvider()
        => (ProviderBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Ftp";

    private void SelectProvider(string provider)
    {
        foreach (var item in ProviderBox.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Content?.ToString(), provider, StringComparison.OrdinalIgnoreCase))
            {
                ProviderBox.SelectedItem = item;
                break;
            }
        if (ProviderBox.SelectedItem is null) ProviderBox.SelectedIndex = 0;
        UpdatePanels();
    }

    private static string? Nz(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
