using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;
using ForgeTekUpdatePackager.Services.Publishing;

namespace ForgeTekUpdatePackager.Dialogs;

/// <summary>Uploads a generated setup .exe to the bundle's own publish target (separate from the apps'
/// update settings) and shows the resulting public URL. Does not touch the update catalog.</summary>
public partial class PublishSetupWindow : Window
{
    private readonly IPublishService _publish;
    private readonly ISetupStorageService _storage;
    private readonly IApprovalService? _approval;
    private readonly ISessionService? _session;
    private readonly bool _requireApproval;
    private readonly string _appKey;
    private readonly GeneratedSetupRecord _record;
    private readonly AppSettings? _target;   // the bundle's publish profile projected for the transports
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _isGitHub;
    private string _fileName = "";

    public PublishSetupWindow(IPublishService publish, ISetupStorageService storage,
        string appKey, PublishProfile? profile, GeneratedSetupRecord record,
        IApprovalService? approval = null, ISessionService? session = null, bool requireApproval = false)
    {
        InitializeComponent();
        _publish = publish;
        _storage = storage;
        _approval = approval;
        _session = session;
        _requireApproval = requireApproval;
        _appKey = appKey;
        _record = record;
        _target = profile?.ToAppSettings();

        var fileName = Path.GetFileName(record.OutputPath);
        SubtitleText.Text = $"{fileName} · v{record.Version}";

        if (profile is null || !profile.IsConfigured() || _target is null)
        {
            ProviderText.Text = "(not configured)";
            TargetText.Text = "—";
            StatusText.Text = "No setup-publish target configured.";
            Log("Set this bundle's publish target with \"Publish settings…\" (next to Generate), then publish.");
            PublishBtn.IsEnabled = false;
            return;
        }

        _fileName = fileName;
        ProviderText.Text = _publish.ProviderName(_target);
        TargetText.Text = _publish.RemoteTarget(_target, _appKey, record.Version, fileName);

        // GitHub Releases: the tag varies per release, so prefill the resolved tag and let the user edit it.
        _isGitHub = string.Equals(_target.PublishProvider, "GitHubReleases", StringComparison.OrdinalIgnoreCase);
        if (_isGitHub)
        {
            var pattern = string.IsNullOrWhiteSpace(_target.GitHubReleaseTag) ? "v{version}" : _target.GitHubReleaseTag!;
            TagRow.Visibility = Visibility.Visible;
            TagBox.Text = pattern.Replace("{version}", record.Version);   // triggers Tag_TextChanged
        }

        // Already published earlier? Show the URL and offer to unpublish (re-publishing also allowed).
        if (!string.IsNullOrWhiteSpace(record.PublishedUrl))
        {
            ShowPublished(record.PublishedUrl!);
            var when = record.PublishedDate is { } d ? $" on {d:yyyy-MM-dd HH:mm}" : "";
            StatusText.Text = $"Published to {record.PublishedProvider ?? _publish.ProviderName(_target)}{when}";
        }

        if (!File.Exists(record.OutputPath))
        {
            if (string.IsNullOrWhiteSpace(record.PublishedUrl))
                StatusText.Text = "Setup file not found.";
            Log($"The setup file is missing:\n{record.OutputPath}");
            PublishBtn.IsEnabled = false;
        }

        // Release gate: a setup can't be published until an Admin and a QA Tester have approved it
        // (only enforced when access protection is on).
        if (!IsApproved)
        {
            PublishBtn.IsEnabled = false;
            StatusText.Text = $"Needs Admin + QA Tester approval — {_approval!.ApprovalsSatisfied(ApprovalTargetKey)} of 2.";
            Log("This setup must be approved by an Admin and a QA Tester before it can be published.");
        }
    }

    // ── Release approval gate ────────────────────────────────────────────
    private string ApprovalTargetKey => ReleaseApproval.ForSetup(_record.BundleId, _record.Id);
    private bool ApprovalRequired => _requireApproval && _session is { IsProtected: true } && _approval is not null;
    private bool IsApproved => !ApprovalRequired || _approval!.IsApproved(ApprovalTargetKey);

    private void ShowPublished(string url)
    {
        UrlBox.Text = url;
        ResultPanel.Visibility = Visibility.Visible;
        UnpublishBtn.Visibility = _target is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private string NormalizedTag()
        => string.IsNullOrWhiteSpace(TagBox.Text) ? "v{version}" : TagBox.Text.Trim();

    // GitHub tag is editable per release; keep the target + displayed remote path in sync.
    private void Tag_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_target is null || !_isGitHub) return;
        _target.GitHubReleaseTag = NormalizedTag();
        try { TargetText.Text = _publish.RemoteTarget(_target, _appKey, _record.Version, _fileName); }
        catch { }
    }

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _target is null) return;
        if (!IsApproved)
        {
            StatusText.Text = $"Needs Admin + QA Tester approval — {_approval!.ApprovalsSatisfied(ApprovalTargetKey)} of 2.";
            return;
        }
        if (_isGitHub) _target.GitHubReleaseTag = NormalizedTag();

        _busy = true;
        PublishBtn.IsEnabled = false;
        StatusText.Text = "Publishing…";
        _cts = new CancellationTokenSource();

        var fileName = Path.GetFileName(_record.OutputPath);
        Log($"Uploading {fileName} to {_publish.ProviderName(_target)}…");

        var total = TryFileSize(_record.OutputPath);
        UploadBar.IsIndeterminate = true;   // spins until the first byte report arrives
        UploadBar.Value = 0;
        UploadBar.Visibility = Visibility.Visible;

        try
        {
            var progress = new Progress<string>(Append);
            var bytes = new Progress<long>(sent =>
            {
                if (total > 0)
                {
                    UploadBar.IsIndeterminate = false;
                    UploadBar.Value = Math.Min(100, sent * 100.0 / total);
                }
            });
            // Off the UI thread (like the dashboard's check) so the transport can't deadlock the dispatcher.
            var token = _cts.Token;
            var url = await Task.Run(() => _publish.UploadArtifactAsync(_target!, _appKey, _record.Version,
                _record.OutputPath, fileName, progress, token, bytes), token);

            _record.PublishedUrl = url;
            _record.PublishedProvider = _publish.ProviderName(_target);
            _record.PublishedDate = DateTime.Now;
            try { _storage.UpdateHistory(_record); } catch { }

            ShowPublished(url);
            StatusText.Text = "✓ Published";
            Append($"Done. Public URL:\n{url}");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Publish failed";
            Append($"Error: {ex.Message}");
            PublishBtn.IsEnabled = true;
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async void Unpublish_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _target is null) return;

        var confirm = MessageBox.Show(this,
            "Remove the published setup from the target?\nThis deletes the uploaded installer file.",
            "Unpublish Setup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _busy = true;
        UnpublishBtn.IsEnabled = false;
        PublishBtn.IsEnabled = false;
        StatusText.Text = "Removing…";
        _cts = new CancellationTokenSource();

        var fileName = Path.GetFileName(_record.OutputPath);
        try
        {
            var progress = new Progress<string>(Append);
            var token = _cts.Token;
            await Task.Run(() => _publish.DeleteArtifactAsync(_target!, _appKey, _record.Version, fileName, progress, token), token);

            _record.PublishedUrl = null;
            _record.PublishedProvider = null;
            _record.PublishedDate = null;
            try { _storage.UpdateHistory(_record); } catch { }

            ResultPanel.Visibility = Visibility.Collapsed;
            UnpublishBtn.Visibility = Visibility.Collapsed;
            StatusText.Text = "✓ Unpublished";
            Append("Removed from the publish target.");
            PublishBtn.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled";
            PublishBtn.IsEnabled = true;
            UnpublishBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Unpublish failed";
            Append($"Error: {ex.Message}");
            PublishBtn.IsEnabled = true;
            UnpublishBtn.IsEnabled = true;
        }
        finally
        {
            UploadBar.Visibility = Visibility.Collapsed;
            UploadBar.IsIndeterminate = false;
            _busy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static long TryFileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(UrlBox.Text))
        {
            try { Clipboard.SetText(UrlBox.Text); StatusText.Text = "URL copied"; } catch { }
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(UrlBox.Text))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = UrlBox.Text, UseShellExecute = true }); }
            catch { }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
    }

    private void Log(string text) => LogText.Text = text;

    private void Append(string line)
    {
        LogText.Text = string.IsNullOrEmpty(LogText.Text) ? line : LogText.Text + "\n" + line;
        LogScroller.ScrollToEnd();
    }
}
