using System.Threading;
using System.Windows;
using System.Windows.Media;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.Dialogs;

/// <summary>Compares a local project working copy against a GitHub repo's tracked source (git blob
/// SHA-1), to show whether the local project matches what's on GitHub.</summary>
public partial class GitHubCompareWindow : Window
{
    private readonly IGitHubService _github;
    private readonly ISourceCompareService _compare;
    private readonly string _repo;
    private readonly string? _token;
    private ComparisonReport? _report;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public GitHubCompareWindow(IGitHubService github, ISourceCompareService compare,
        string repo, string? token, string localDir, string appName)
    {
        InitializeComponent();
        _github = github;
        _compare = compare;
        _repo = repo;
        _token = token;
        SubtitleText.Text = $"{appName} — local project vs {repo}.";
        LocalBox.Text = localDir ?? string.Empty;
        Loaded += async (_, _) => await LoadBranchesAsync();
    }

    private async Task LoadBranchesAsync()
    {
        StatusText.Text = "Loading branches…";
        try
        {
            var branches = await _github.GetBranchesAsync(_repo, _token);
            BranchBox.ItemsSource = branches;
            BranchBox.SelectedItem = branches.FirstOrDefault(b => b is "main" or "master")
                                     ?? branches.FirstOrDefault();
            StatusText.Text = branches.Count == 0 ? "No branches found." : "Pick a branch and Compare.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not load branches: {ex.Message}";
        }
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var branch = BranchBox.SelectedItem as string;
        var local = LocalBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(branch)) { StatusText.Text = "Pick a branch."; return; }
        if (string.IsNullOrWhiteSpace(local)) { StatusText.Text = "Choose the local project folder."; return; }

        _busy = true;
        CompareBtn.IsEnabled = false;
        ResultsList.ItemsSource = null;
        EmptyText.Visibility = Visibility.Collapsed;
        SummaryText.Visibility = Visibility.Collapsed;
        StatusText.Text = "Fetching repo tree…";
        _cts = new CancellationTokenSource();

        try
        {
            var tree = await _github.GetRepoTreeAsync(_repo, _token, branch, _cts.Token);
            var progress = new Progress<string>(s => StatusText.Text = s);
            _report = await _compare.CompareWithTreeAsync(local, tree, progress, _cts.Token);
            Render();
        }
        catch (OperationCanceledException) { StatusText.Text = "Cancelled."; }
        catch (Exception ex) { StatusText.Text = $"Compare failed: {ex.Message}"; }
        finally
        {
            _busy = false;
            CompareBtn.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Render()
    {
        if (_report is null) return;
        if (!_report.Ran)
        {
            EmptyText.Text = _report.Error ?? "Could not compare.";
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        var rows = _report.Rows
            .Where(r => DiffOnly.IsChecked != true || r.Status != CompareStatus.Identical)
            .OrderByDescending(r => RankOf(r.Status))
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(r => new Row(r))
            .ToList();
        ResultsList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _report.Total == 0 ? "No tracked files found." : "No differences.";

        var inSync = _report.Identical;
        var modified = _report.Differs;
        var missing = _report.Partial;
        SummaryText.Visibility = Visibility.Visible;
        if (_report.AllIdentical)
            (SummaryText.Text, SummaryText.Foreground) = ("✓  Local project matches GitHub.", (Brush)FindResource("AddedBrush"));
        else
            (SummaryText.Text, SummaryText.Foreground) =
                ($"{modified} modified · {missing} missing locally · {inSync} in sync", Rgb(255, 209, 102));

        StatusText.Text = $"Compared {_report.Total} tracked file(s).";
    }

    private static int RankOf(CompareStatus s) => s switch
    {
        CompareStatus.Differs => 2,
        CompareStatus.Partial => 1,
        _ => 0,
    };

    private void DiffOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (_report is not null && _report.Ran) Render();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select the local project folder" };
        if (!string.IsNullOrWhiteSpace(LocalBox.Text)) dlg.InitialDirectory = LocalBox.Text;
        if (dlg.ShowDialog() == true) LocalBox.Text = dlg.FolderName;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
        Close();
    }

    private static SolidColorBrush Rgb(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    /// <summary>Display row: local vs repo blob hashes + status (Files=local, GitHub=remote).</summary>
    private sealed class Row(SourceFileRow r)
    {
        public string Path => r.Path;
        public string Local => Short(r.FilesHash);
        public string Repo => Short(r.GitHubHash);
        public string Status => r.Status switch
        {
            CompareStatus.Identical => "In sync",
            CompareStatus.Differs => "Modified",
            _ => "Missing locally",
        };
        public Brush StatusBrush => r.Status switch
        {
            CompareStatus.Differs => new SolidColorBrush(Color.FromRgb(255, 107, 107)),
            CompareStatus.Partial => new SolidColorBrush(Color.FromRgb(255, 209, 102)),
            _ => new SolidColorBrush(Color.FromRgb(48, 209, 88)),
        };

        private static string Short(string? hash)
            => string.IsNullOrEmpty(hash) ? "—" : hash[..Math.Min(7, hash.Length)];
    }
}
