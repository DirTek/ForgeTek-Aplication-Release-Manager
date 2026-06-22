using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.Dialogs;

/// <summary>Compares an app's tracked files, its solution build output, and its GitHub build output
/// by relative path + SHA256, flagging files that differ or are missing in a source.</summary>
public partial class SourceCompareWindow : Window
{
    private readonly ISourceCompareService _compare;
    private ComparisonReport? _report;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public SourceCompareWindow(ISourceCompareService compare, string appName,
        string? filesDir, string? slnDir, string? githubDir)
    {
        InitializeComponent();
        _compare = compare;
        SubtitleText.Text = $"{appName} — verify files, solution build, and GitHub build match.";
        FilesBox.Text = filesDir ?? string.Empty;
        SlnBox.Text = slnDir ?? string.Empty;
        GitHubBox.Text = githubDir ?? string.Empty;
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var files = Nz(FilesBox.Text);
        var sln = Nz(SlnBox.Text);
        var github = Nz(GitHubBox.Text);
        if (new[] { files, sln, github }.Count(p => p is not null) < 2)
        {
            StatusBar("Fill in at least two source paths to compare.");
            return;
        }

        _busy = true;
        CompareBtn.IsEnabled = false;
        VerdictPanel.Visibility = Visibility.Collapsed;
        ResultsList.ItemsSource = null;
        EmptyText.Visibility = Visibility.Collapsed;
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<string>(StatusBar);
            _report = await _compare.CompareAsync(files, sln, github, progress, _cts.Token);
            Render();
        }
        catch (OperationCanceledException) { StatusBar("Cancelled."); }
        catch (Exception ex) { StatusBar($"Compare failed: {ex.Message}"); }
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
            .OrderByDescending(r => (int)RankOf(r.Status))   // Differs/Partial first
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(r => new Row(r))
            .ToList();
        ResultsList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _report.Total == 0 ? "No files found in the sources." : "No differences.";

        IdenticalText.Text = $"Identical: {_report.Identical}";
        DiffersText.Text = $"Differs: {_report.Differs}";
        PartialText.Text = $"Missing in some: {_report.Partial}";

        VerdictPanel.Visibility = Visibility.Visible;
        if (_report.AllIdentical)
            (VerdictText.Text, VerdictText.Foreground) = ("✓  All sources match.", (Brush)FindResource("AddedBrush"));
        else if (_report.Differs > 0)
            (VerdictText.Text, VerdictText.Foreground) = ($"✗  {_report.Differs} file(s) differ, {_report.Partial} missing in a source.", Rgb(255, 107, 107));
        else
            (VerdictText.Text, VerdictText.Foreground) = ($"⚠  {_report.Partial} file(s) missing in a source.", Rgb(255, 209, 102));

        var sources = new List<string>();
        if (_report.HasFiles) sources.Add("files");
        if (_report.HasSln) sources.Add("solution");
        if (_report.HasGitHub) sources.Add("GitHub");
        StatusBar($"Compared {_report.Total} file(s) across: {string.Join(", ", sources)}.");
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
        if (sender is not Button btn || btn.Tag is not string boxName) return;
        var box = FindName(boxName) as TextBox;
        if (box is null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select folder" };
        if (!string.IsNullOrWhiteSpace(box.Text)) dlg.InitialDirectory = box.Text;
        if (dlg.ShowDialog() == true) box.Text = dlg.FolderName;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
        Close();
    }

    private static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static SolidColorBrush Rgb(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private void StatusBar(string text) => StatusText.Text = text;

    /// <summary>Display row: short hashes per source + status colour.</summary>
    private sealed class Row(SourceFileRow r)
    {
        public string Path => r.Path;
        public string Files => Short(r.FilesHash);
        public string Sln => Short(r.SlnHash);
        public string GitHub => Short(r.GitHubHash);
        public string Status => r.Status switch
        {
            CompareStatus.Identical => "Identical",
            CompareStatus.Differs => "Differs",
            _ => "Missing",
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
