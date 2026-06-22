using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.Dialogs;

public partial class ReleaseNotesWindow : Window
{
    private readonly IGitHubService _github;
    private readonly IChangelogService _changelog;
    private readonly string _repo;
    private readonly string? _token;
    private readonly string? _appFolder;
    private readonly string? _solutionFolder;
    private readonly string? _appName;
    // Save-to destinations (label → folder), shown in the "Save to" picker.
    private readonly Dictionary<string, string> _saveDestinations = new(StringComparer.OrdinalIgnoreCase);

    // The suggestions pool plus one collection per section. Notes are dragged between them.
    private readonly ObservableCollection<string> _suggestions = [];
    private readonly ObservableCollection<string> _added = [];
    private readonly ObservableCollection<string> _changed = [];
    private readonly ObservableCollection<string> _improved = [];
    private readonly ObservableCollection<string> _removed = [];
    private readonly ObservableCollection<string> _fixed = [];

    // Maps each list control to its backing collection (for drag-drop moves).
    private readonly Dictionary<ListBox, ObservableCollection<string>> _lists = new();

    // Active drag payload (intra-window, so we keep references rather than serialize).
    private Point _dragStart;
    private string? _dragItem;
    private ObservableCollection<string>? _dragSource;

    public ReleaseNotesWindow(IGitHubService github, IChangelogService changelog, string repo, string? token,
        string? appFolder = null, string? appName = null, string? prefillVersion = null,
        string? solutionFolder = null)
    {
        InitializeComponent();
        _github = github;
        _changelog = changelog;
        _repo = repo;
        _token = token;
        _appFolder = appFolder;
        _solutionFolder = solutionFolder;
        _appName = appName;

        if (!string.IsNullOrWhiteSpace(prefillVersion)) VersionBox.Text = prefillVersion;
        if (!string.IsNullOrWhiteSpace(appFolder)) ChangelogBtn.Visibility = Visibility.Visible;

        // Offer the app folder and/or the solution folder as quick save destinations.
        if (!string.IsNullOrWhiteSpace(appFolder)) _saveDestinations["App folder"] = appFolder!;
        if (!string.IsNullOrWhiteSpace(solutionFolder)) _saveDestinations["Solution folder"] = solutionFolder!;
        if (_saveDestinations.Count > 0)
        {
            SaveDestBox.ItemsSource = _saveDestinations.Keys.ToList();
            SaveDestBox.SelectedIndex = 0;
            SaveDestBox.Visibility = Visibility.Visible;
            SaveDestLabel.Visibility = Visibility.Visible;
        }

        SuggestionsList.ItemsSource = _suggestions;
        AddedList.ItemsSource = _added;
        ChangedList.ItemsSource = _changed;
        ImprovedList.ItemsSource = _improved;
        RemovedList.ItemsSource = _removed;
        FixedList.ItemsSource = _fixed;

        foreach (var (list, coll) in new[]
        {
            (SuggestionsList, _suggestions), (AddedList, _added), (ChangedList, _changed),
            (ImprovedList, _improved), (RemovedList, _removed), (FixedList, _fixed),
        })
        {
            _lists[list] = coll;
            list.PreviewMouseLeftButtonDown += List_PreviewMouseLeftButtonDown;
            list.PreviewMouseMove += List_PreviewMouseMove;
            list.DragOver += List_DragOver;
            list.Drop += List_Drop;
        }

        DatePick.SelectedDate = DateTime.Today;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Spinner.Visibility = Visibility.Visible;
        StatusText.Text = "Loading tags and branches…";
        GenerateBtn.IsEnabled = false;
        try
        {
            var tags = await _github.GetTagsAsync(_repo, _token);
            IReadOnlyList<string> branches;
            try { branches = await _github.GetBranchesAsync(_repo, _token); }
            catch { branches = []; }

            var refs = tags.Concat(branches).ToList();
            FromCombo.ItemsSource = refs.ToList();
            ToCombo.ItemsSource = refs.ToList();

            if (tags.Count > 0)
            {
                ToCombo.SelectedItem = tags[0];
                if (tags.Count > 1) FromCombo.SelectedItem = tags[1];
                StatusText.Text = $"{tags.Count} tags, {branches.Count} branches loaded.";
            }
            else
            {
                var main = branches.FirstOrDefault(b => b is "main" or "master") ?? branches.FirstOrDefault();
                if (main is not null) ToCombo.SelectedItem = main;
                StatusText.Text = branches.Count > 0
                    ? "No tags — generating from recent commits on the selected branch."
                    : "No tags or branches found — type a ref manually.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not load refs: {ex.Message}";
        }
        finally
        {
            Spinner.Visibility = Visibility.Collapsed;
            GenerateBtn.IsEnabled = true;
        }

        // Show the repo's last commit as a hint (non-fatal if it fails).
        try
        {
            var last = await _github.GetLastCommitAsync(_repo, _token);
            if (last is not null && !string.IsNullOrWhiteSpace(last.Message))
            {
                LastCommitText.Text = $"Last commit: {last.Message}  —  {last.Meta}";
                LastCommitBox.Visibility = Visibility.Visible;
            }
        }
        catch { /* ignore */ }
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        var from = (FromCombo.Text ?? string.Empty).Trim();
        var to = (ToCombo.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(to))
        {
            StatusText.Text = "Pick a 'to' tag or branch.";
            return;
        }

        var useRecent = string.IsNullOrWhiteSpace(from)
                        || string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        Spinner.Visibility = Visibility.Visible;
        GenerateBtn.IsEnabled = false;
        try
        {
            IReadOnlyList<CommitChange> changes;
            if (useRecent)
            {
                if (!int.TryParse(CountBox.Text, out var count) || count < 1) count = 20;
                StatusText.Text = $"Reading the last {count} commits on {to}…";
                changes = await _github.GetRecentChangesAsync(_repo, _token, to, count);
            }
            else
            {
                StatusText.Text = $"Comparing {from}…{to}…";
                changes = await _github.GetCompareChangesAsync(_repo, _token, from, to);
            }

            // Refill the pool; pre-sort the confident guesses (feat→Added, fix→Fixed).
            foreach (var c in new[] { _suggestions, _added, _changed, _improved, _removed, _fixed }) c.Clear();
            foreach (var c in changes)
            {
                switch (c.Suggested)
                {
                    case "Added":    _added.Add(c.Text); break;
                    case "Changed":  _changed.Add(c.Text); break;
                    case "Improved": _improved.Add(c.Text); break;
                    case "Removed":  _removed.Add(c.Text); break;
                    case "Fixed":    _fixed.Add(c.Text); break;
                    default:         _suggestions.Add(c.Text); break;
                }
            }

            StatusText.Text = changes.Count == 0
                ? "No commits found — try a different range or branch."
                : $"{changes.Count} commits. Drag each line into a section, then Build Draft.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Generation failed: {ex.Message}";
        }
        finally
        {
            Spinner.Visibility = Visibility.Collapsed;
            GenerateBtn.IsEnabled = true;
        }
    }

    // ── Drag and drop between lists ──────────────────────────────────────────
    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = ItemUnder(e.OriginalSource as DependencyObject);
        _dragSource = _dragItem is not null ? _lists[(ListBox)sender] : null;
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null || _dragSource is null) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        DragDrop.DoDragDrop((ListBox)sender, new DataObject(DataFormats.StringFormat, _dragItem), DragDropEffects.Move);
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _dragItem is not null ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void List_Drop(object sender, DragEventArgs e)
    {
        if (_dragItem is null || _dragSource is null) return;
        var target = _lists[(ListBox)sender];
        if (!ReferenceEquals(target, _dragSource))
        {
            _dragSource.Remove(_dragItem);
            if (!target.Contains(_dragItem)) target.Add(_dragItem);
        }
        _dragItem = null;
        _dragSource = null;
    }

    // Walks up the visual tree from the drag origin to the ListBoxItem's data (the note string).
    private static string? ItemUnder(DependencyObject? src)
    {
        while (src is not null and not ListBoxItem)
            src = VisualTreeHelper.GetParent(src);
        return (src as ListBoxItem)?.DataContext as string;
    }

    // ── Compose the final draft ───────────────────────────────────────────────
    private void BuildDraft_Click(object sender, RoutedEventArgs e)
    {
        var date = (DatePick.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd");
        var version = VersionBox.Text.Trim();
        var title = string.IsNullOrWhiteSpace(version) ? "Release" : $"Version {version}";

        var sb = new StringBuilder();
        sb.AppendLine($"## {title} - {date}").AppendLine();
        Section(sb, "Added", _added);
        Section(sb, "Changed", _changed);
        Section(sb, "Improved", _improved);
        Section(sb, "Removed", _removed);
        Section(sb, "Fixed", _fixed);

        if (_added.Count + _changed.Count + _improved.Count + _removed.Count + _fixed.Count == 0)
        {
            StatusText.Text = "Nothing categorized yet — drag commits into the sections first.";
            return;
        }

        NotesBox.Text = sb.ToString().TrimEnd() + "\n";
        CopyBtn.IsEnabled = true;
        SaveBtn.IsEnabled = true;
        PublishBtn.IsEnabled = true;
        ChangelogBtn.IsEnabled = true;
        Tabs.SelectedIndex = 1;   // jump to the Draft tab
        StatusText.Text = "Draft ready — review and edit, then copy, save, or publish.";
    }

    private static void Section(StringBuilder sb, string header, ObservableCollection<string> items)
    {
        if (items.Count == 0) return;
        sb.AppendLine($"### {header}");
        foreach (var item in items) sb.AppendLine($"- {item}");
        sb.AppendLine();
    }

    // ── Output actions ─────────────────────────────────────────────────────────
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(NotesBox.Text); StatusText.Text = "Copied to clipboard."; }
        catch (Exception ex) { StatusText.Text = $"Copy failed: {ex.Message}"; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var version = VersionBox.Text.Trim();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save release notes",
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"release-notes-{(string.IsNullOrWhiteSpace(version) ? "draft" : version)}.md",
        };
        // Open the dialog in the chosen destination (the app folder or the solution folder).
        if (SaveDestBox.SelectedItem is string dest && _saveDestinations.TryGetValue(dest, out var folder)
            && Directory.Exists(folder))
            dlg.InitialDirectory = folder;

        if (dlg.ShowDialog() != true) return;
        try { File.WriteAllText(dlg.FileName, NotesBox.Text); StatusText.Text = $"Saved to {dlg.FileName}"; }
        catch (Exception ex) { StatusText.Text = $"Save failed: {ex.Message}"; }
    }

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        // Prefer the Version field as the release tag; fall back to the "to" ref.
        var tag = VersionBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(tag)) tag = (ToCombo.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(tag))
        {
            StatusText.Text = "Set a Version (used as the release tag) before publishing.";
            return;
        }

        var confirm = new ConfirmDialog("Publish Release Notes",
            $"Create or update the GitHub release for tag '{tag}' with these notes?",
            "Publish", isDanger: false) { Owner = this };
        if (confirm.ShowDialog() != true) return;

        Spinner.Visibility = Visibility.Visible;
        StatusText.Text = $"Publishing release '{tag}'…";
        PublishBtn.IsEnabled = false;
        try
        {
            await _github.PublishReleaseNotesAsync(_repo, _token, tag, NotesBox.Text);
            StatusText.Text = $"Published release '{tag}' on GitHub.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Publish failed: {ex.Message}";
        }
        finally
        {
            Spinner.Visibility = Visibility.Collapsed;
            PublishBtn.IsEnabled = true;
        }
    }

    private void AddToChangelog_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_appFolder)) return;
        var section = NotesBox.Text.Trim();
        if (section.Length == 0) { StatusText.Text = "Build the draft first."; return; }

        try
        {
            var path = _changelog.FindChangelogFile(_appFolder)
                       ?? Path.Combine(_appFolder, "CHANGELOG.md");
            var existing = File.Exists(path) ? File.ReadAllText(path) : null;
            File.WriteAllText(path, _changelog.BuildChangelog(existing, section, _appName ?? string.Empty));
            StatusText.Text = $"Added to {Path.GetFileName(path)} (newest entry on top).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not update changelog: {ex.Message}";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
