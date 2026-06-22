using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using ForgeTekApplicationReleaseManager.Models;
using ForgeTekApplicationReleaseManager.Services;

namespace ForgeTekApplicationReleaseManager.Dialogs;

/// <summary>Resolves a project's third-party NuGet licenses, applies the allow/warn/block policy, and
/// exports a Third-Party Components report.</summary>
public partial class LicenseScanWindow : Window
{
    private readonly ILicenseScanService _scan;
    private readonly ISettingsService _settings;
    private readonly string _appName;
    private LicenseReport? _report;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public LicenseScanWindow(ILicenseScanService scan, ISettingsService settings,
        string? defaultPath, string appName)
    {
        InitializeComponent();
        _scan = scan;
        _settings = settings;
        _appName = appName;

        SubtitleText.Text = $"{appName} — third-party NuGet packages and their licenses.";
        PathBox.Text = defaultPath ?? string.Empty;

        UnknownPolicy.ItemsSource = Enum.GetNames<PolicyAction>();
        LoadPolicy(_settings.Global.License);
    }

    private void LoadPolicy(LicensePolicy p)
    {
        AllowedBox.Text = string.Join(", ", p.Allowed);
        WarnBox.Text = string.Join(", ", p.Warn);
        BlockBox.Text = string.Join(", ", p.Block);
        UnknownPolicy.SelectedItem = p.Unknown.ToString();
    }

    private LicensePolicy CurrentPolicy() => new()
    {
        Allowed = SplitList(AllowedBox.Text),
        Warn = SplitList(WarnBox.Text),
        Block = SplitList(BlockBox.Text),
        Unknown = Enum.TryParse<PolicyAction>(UnknownPolicy.SelectedItem as string, out var a) ? a : PolicyAction.Warn,
    };

    private static List<string> SplitList(string text)
        => text.Split([',', '\n', '\r', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .ToList();

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var path = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText.Text = "Choose a project, solution, or repo folder first.";
            return;
        }

        _busy = true;
        ScanBtn.IsEnabled = false;
        StatusText.Text = "Scanning…";
        ResultsList.ItemsSource = null;
        EmptyText.Visibility = Visibility.Collapsed;
        VerdictPanel.Visibility = Visibility.Collapsed;
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<string>(line => StatusText.Text = line);
            _report = await _scan.ScanAsync(path, progress, _cts.Token);
            Render();
        }
        catch (OperationCanceledException) { StatusText.Text = "Cancelled"; }
        catch (Exception ex) { StatusText.Text = $"Scan failed: {ex.Message}"; }
        finally
        {
            _busy = false;
            ScanBtn.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Render()
    {
        if (_report is null) return;
        // A fresh scan lists the licenses but does NOT pass judgement — the gate is applied on demand.
        VerdictPanel.Visibility = Visibility.Collapsed;

        if (!_report.Ran)
        {
            StatusText.Text = _report.Error ?? "Scan could not run.";
            EmptyText.Text = _report.Error ?? "Scan could not run.";
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        // Show the components with no verdict yet (Status column shows "—").
        var rows = _report.Components
            .OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Select(c => new LicenseRow(c, action: null))
            .ToList();
        ResultsList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = "No NuGet dependencies found.";
        StatusText.Text = $"Scanned {_report.ScannedPath} · {_report.Total} package(s) — review, then Apply policy or Close.";
    }

    // The block/warn verdict is never automatic: the user reviews licenses and chooses to apply it.
    private void ApplyPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null || !_report.Ran)
        {
            StatusText.Text = "Run a scan first.";
            return;
        }

        var policy = CurrentPolicy();
        var rows = _report.Components.Select(c => new LicenseRow(c, policy.ActionFor(c.License)))
            .OrderByDescending(r => (int)(r.Action ?? PolicyAction.Allow))
            .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ResultsList.ItemsSource = rows;

        var blocked = rows.Count(r => r.Action == PolicyAction.Block);
        var warned = rows.Count(r => r.Action == PolicyAction.Warn);
        var allowed = rows.Count(r => r.Action == PolicyAction.Allow);
        BlockedText.Text = $"Blocked: {blocked}";
        WarnText.Text = $"Warn: {warned}";
        AllowedText.Text = $"Allowed: {allowed}";

        var verdict = policy.Evaluate(_report);
        VerdictPanel.Visibility = Visibility.Visible;
        (VerdictText.Text, VerdictText.Foreground) = verdict switch
        {
            PolicyAction.Block => ($"✗  Release blocked — {blocked} forbidden license(s).", Brush(255, 107, 107)),
            PolicyAction.Warn => ($"⚠  {warned} license(s) need review.", Brush(255, 209, 102)),
            _ => ("✓  All licenses allowed by policy.", (Brush)FindResource("AddedBrush")),
        };
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private void SavePolicy_Click(object sender, RoutedEventArgs e)
    {
        _settings.Global.License = CurrentPolicy();
        _settings.SaveGlobal();
        StatusText.Text = "Policy saved.";
        if (VerdictPanel.Visibility == Visibility.Visible) ApplyPolicy_Click(sender, e);
    }

    private void ExportTxt_Click(object sender, RoutedEventArgs e)
        => Export("Text report (*.txt)|*.txt", "licenses.txt", () => _scan.BuildText(_report!, _appName));

    private void ExportHtml_Click(object sender, RoutedEventArgs e)
        => Export("HTML report (*.html)|*.html", "licenses.html", () => _scan.BuildHtml(_report!, _appName));

    private void Export(string filter, string defaultName, Func<string> build)
    {
        if (_report is null || !_report.Ran)
        {
            StatusText.Text = "Run a scan first.";
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = defaultName };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, build());
            StatusText.Text = $"Saved {dlg.FileName}";
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\""); } catch { }
        }
        catch (Exception ex) { StatusText.Text = $"Export failed: {ex.Message}"; }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select the project / repo folder" };
        if (!string.IsNullOrWhiteSpace(PathBox.Text)) dlg.InitialDirectory = PathBox.Text;
        if (dlg.ShowDialog() == true) PathBox.Text = dlg.FolderName;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
        Close();
    }

    /// <summary>Display row: a component plus its policy status/colour. Action is null until the user
    /// applies the policy (Status then shows "—").</summary>
    private sealed class LicenseRow(LicenseComponent c, PolicyAction? action)
    {
        public string Id => c.Id;
        public string Version => c.Version;
        public string License => c.License;
        public bool Transitive => c.Transitive;
        public PolicyAction? Action { get; } = action;
        public string Status => Action switch
        {
            PolicyAction.Block => "Blocked",
            PolicyAction.Warn => "Warn",
            PolicyAction.Allow => "OK",
            _ => "—",
        };
        public Brush StatusBrush => Action switch
        {
            PolicyAction.Block => new SolidColorBrush(Color.FromRgb(255, 107, 107)),
            PolicyAction.Warn => new SolidColorBrush(Color.FromRgb(255, 209, 102)),
            PolicyAction.Allow => new SolidColorBrush(Color.FromRgb(48, 209, 88)),
            _ => new SolidColorBrush(Color.FromRgb(152, 152, 157)),
        };
    }
}
