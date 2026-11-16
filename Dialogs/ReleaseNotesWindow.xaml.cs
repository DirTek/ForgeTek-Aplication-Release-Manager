using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ForgeTekApplicationReleaseManager.Services;
using static ForgeTekApplicationReleaseManager.Services.LocalizationService;

namespace ForgeTekApplicationReleaseManager.Dialogs;

public partial class ReleaseNotesWindow : Window
{
    private const int CommitPickerCount = 100;

    private readonly IGitHubService _github;
    private readonly IChangelogService _changelog;
    private readonly string _repo;
    private readonly string? _token;
    private readonly string? _appFolder;
    private readonly string? _solutionFolder;
    private readonly string? _appName;
    private readonly string? _prefillVersion;
    private readonly string? _previousVersion;
    private readonly DateTime? _previousVersionDate;
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

    // Generation order of every note, so a note sent back to Suggestions returns to its original spot.
    private readonly Dictionary<string, int> _order = new(StringComparer.Ordinal);

    // Everything the From/To pickers offer: tags, branches and recent commits.
    private List<RefItem> _refs = [];

    // Active drag payload (intra-window, so we keep references rather than serialize).
    private Point _dragStart;
    private string? _dragItem;
    private ObservableCollection<string>? _dragSource;

    /// <summary>True once "Add to Changelog" has written the changelog during this session.</summary>
    public bool ChangelogWritten { get; private set; }

    /// <param name="previousVersion">The version released before <paramref name="prefillVersion"/>; its tag
    /// (if the repo has one) becomes the default "from" ref.</param>
    /// <param name="previousVersionDate">When the previous version was scanned; without a tag, the default
    /// "from" is the last commit made before then.</param>
    public ReleaseNotesWindow(IGitHubService github, IChangelogService changelog, string repo, string? token,
        string? appFolder = null, string? appName = null, string? prefillVersion = null,
        string? solutionFolder = null, string? previousVersion = null, DateTime? previousVersionDate = null)
    {
        InitializeComponent();
        _github = github;
        _changelog = changelog;
        _repo = repo;
        _token = token;
        _appFolder = appFolder;
        _solutionFolder = solutionFolder;
        _appName = appName;
        _prefillVersion = prefillVersion;
        _previousVersion = previousVersion;
        _previousVersionDate = previousVersionDate;

        if (!string.IsNullOrWhiteSpace(prefillVersion)) VersionBox.Text = prefillVersion;
        if (!string.IsNullOrWhiteSpace(appFolder) || !string.IsNullOrWhiteSpace(solutionFolder))
            ChangelogBtn.Visibility = Visibility.Visible;

        // Offer the app folder and/or the solution folder as quick save destinations.
        if (!string.IsNullOrWhiteSpace(appFolder)) _saveDestinations[S("Str.RNCB.SaveDestApp")] = appFolder!;
        if (!string.IsNullOrWhiteSpace(solutionFolder)) _saveDestinations[S("Str.RNCB.SaveDestSolution")] = solutionFolder!;
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
            list.PreviewMouseWheel += List_PreviewMouseWheel;
            list.PreviewKeyDown += List_PreviewKeyDown;
            list.DragOver += List_DragOver;
            list.Drop += List_Drop;
        }

        DatePick.SelectedDate = DateTime.Today;
        Loaded += OnLoaded;
    }

    // ── Ref pickers ───────────────────────────────────────────────────────────
    /// <summary>A pickable ref: <see cref="Ref"/> goes to GitHub, <see cref="Label"/> is what the combo shows.</summary>
    private sealed record RefItem(string Ref, string Label)
    {
        public override string ToString() => Label;
    }

    private static string CommitLabel(RepoCommit c)
    {
        var msg = c.Message.Length > 70 ? c.Message[..69] + "…" : c.Message;
        return c.Date is { } d ? $"{c.ShortSha} · {d:yyyy-MM-dd} · {msg}" : $"{c.ShortSha} · {msg}";
    }

    // A tag that names this version: "1.2.3", "v1.2.3", "release-1.2.3" — but not "1.2.30".
    private static string? FindVersionTag(IReadOnlyList<string> tags, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var v = version.Trim();
        return tags.FirstOrDefault(t => string.Equals(t, v, StringComparison.OrdinalIgnoreCase))
            ?? tags.FirstOrDefault(t => string.Equals(t, "v" + v, StringComparison.OrdinalIgnoreCase))
            ?? tags.FirstOrDefault(t => Regex.IsMatch(t, $@"(?<![\d.]){Regex.Escape(v)}(?![\d.])"));
    }

    // The ref to send to GitHub for a picker: the chosen item's ref, or whatever was typed (tag/branch/SHA).
    private string ResolveRef(ComboBox combo)
    {
        var text = (combo.Text ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;
        if (combo.SelectedItem is RefItem sel && sel.Label == text) return sel.Ref;
        return _refs.FirstOrDefault(r => r.Label == text)?.Ref ?? text;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Spinner.Visibility = Visibility.Visible;
        StatusText.Text = S("Str.RNCB.LoadingRefs");
        GenerateBtn.IsEnabled = false;
        try
        {
            var tags = await _github.GetTagsAsync(_repo, _token);
            IReadOnlyList<string> branches;
            try { branches = await _github.GetBranchesAsync(_repo, _token); }
            catch { branches = []; }
            IReadOnlyList<RepoCommit> commits;
            try { commits = await _github.GetCommitsAsync(_repo, _token, null, CommitPickerCount); }
            catch { commits = []; }

            var tagItems    = tags.Select(t => new RefItem(t, t)).ToList();
            var branchItems = branches.Select(b => new RefItem(b, b)).ToList();
            var commitItems = commits.Where(c => c.Sha.Length > 0)
                                     .Select(c => (Commit: c, Item: new RefItem(c.Sha, CommitLabel(c)))).ToList();
            _refs = [.. tagItems, .. branchItems, .. commitItems.Select(x => x.Item)];
            FromCombo.ItemsSource = _refs.ToList();
            ToCombo.ItemsSource = _refs.ToList();

            // TO: this version's tag when it is already tagged, else the default branch (the new version is
            // usually not tagged yet — defaulting to the newest tag would re-list the previous release).
            var toTag = FindVersionTag(tags, _prefillVersion);
            var mainBranch = branches.FirstOrDefault(b => b is "main" or "master") ?? branches.FirstOrDefault();
            RefItem? to = toTag is not null ? tagItems.First(t => t.Ref == toTag)
                : mainBranch is not null ? branchItems.First(b => b.Ref == mainBranch)
                : tagItems.FirstOrDefault();

            // FROM: the previous version's tag; else the last commit made before the previous version was
            // scanned; else (no version context) the tag just older than TO.
            RefItem? from = null;
            if (FindVersionTag(tags, _previousVersion) is { } prevTag)
                from = tagItems.First(t => t.Ref == prevTag);
            else if (_previousVersionDate is { } prevDate)
                from = commitItems.FirstOrDefault(x => x.Commit.Date is { } d && d <= prevDate).Item;
            else if (to is not null && tagItems.IndexOf(to) is var ti and >= 0 && ti + 1 < tagItems.Count)
                from = tagItems[ti + 1];
            else if (to is not null && tagItems.Count > 0 && !tagItems.Contains(to))
                from = tagItems[0];

            if (to is not null) ToCombo.SelectedItem = to;
            if (from is not null) FromCombo.SelectedItem = from;

            StatusText.Text = tags.Count + branches.Count + commitItems.Count == 0
                ? S("Str.RNCB.NoTagsOrBranches")
                : S("Str.RNCB.LoadedFmt", tags.Count, branches.Count, commitItems.Count);
        }
        catch (Exception ex)
        {
            StatusText.Text = S("Str.RNCB.LoadRefsFailedFmt", ex.Message);
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
                LastCommitText.Text = S("Str.RNCB.LastCommitFmt", last.Message, last.Meta);
                LastCommitBox.Visibility = Visibility.Visible;
            }
        }
        catch { /* ignore */ }
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        var from = ResolveRef(FromCombo);
        var to = ResolveRef(ToCombo);
        if (string.IsNullOrWhiteSpace(to))
        {
            StatusText.Text = S("Str.RNCB.PickToRef");
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
                StatusText.Text = S("Str.RNCB.ReadingCommitsFmt", count, ToCombo.Text);
                changes = await _github.GetRecentChangesAsync(_repo, _token, to, count);
            }
            else
            {
                StatusText.Text = S("Str.RNCB.ComparingFmt", FromCombo.Text, ToCombo.Text);
                changes = await _github.GetCompareChangesAsync(_repo, _token, from, to);
            }

            // Lines already in the changelog belong to earlier versions — hide them (optional).
            IReadOnlySet<string> documented = new HashSet<string>();
            if (HideDocumentedBox.IsChecked == true)
            {
                try { documented = _changelog.GetDocumentedEntries(_solutionFolder, _appFolder); }
                catch { /* unreadable changelog — just don't filter */ }
            }

            // Refill the pool; pre-sort the confident guesses (feat→Added, fix→Fixed). Repeated lines
            // (the same note in several commits) are shown once.
            foreach (var c in new[] { _suggestions, _added, _changed, _improved, _removed, _fixed }) c.Clear();
            _order.Clear();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var hidden = 0;
            foreach (var c in changes)
            {
                var key = ChangelogService.NormalizeEntry(c.Text);
                if (key.Length == 0 || !seen.Add(key)) continue;
                if (documented.Contains(key)) { hidden++; continue; }

                _order[c.Text] = _order.Count;
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

            StatusText.Text = _order.Count == 0 && hidden == 0 ? S("Str.RNCB.NoCommits")
                : hidden > 0 ? S("Str.RNCB.CommitsDragHiddenFmt", _order.Count, hidden)
                : S("Str.RNCB.CommitsDragFmt", _order.Count);
        }
        catch (Exception ex)
        {
            StatusText.Text = S("Str.RNCB.GenFailedFmt", ex.Message);
        }
        finally
        {
            Spinner.Visibility = Visibility.Collapsed;
            GenerateBtn.IsEnabled = true;
        }
    }

    // ── Moving notes ──────────────────────────────────────────────────────────
    // Moves a note to position `index` of `target` (reordering when source == target).
    private static void MoveNote(string item, ObservableCollection<string> source,
        ObservableCollection<string> target, int index)
    {
        var oldIndex = source.IndexOf(item);
        if (oldIndex < 0) return;

        if (ReferenceEquals(source, target))
        {
            if (index > oldIndex) index--;   // the item's own slot disappears when it is lifted out
            index = Math.Clamp(index, 0, target.Count - 1);
            if (index != oldIndex) target.Move(oldIndex, index);
            return;
        }

        source.RemoveAt(oldIndex);
        target.Insert(Math.Clamp(index, 0, target.Count), item);
    }

    // Sends a note from a section back to Suggestions, at its original generated position.
    private void ReturnToSuggestions(string note, ObservableCollection<string> source)
    {
        if (ReferenceEquals(source, _suggestions)) return;
        var rank = _order.TryGetValue(note, out var r) ? r : int.MaxValue;
        var pos = _suggestions.TakeWhile(s => (_order.TryGetValue(s, out var o) ? o : int.MaxValue) < rank).Count();
        MoveNote(note, source, _suggestions, pos);
    }

    private void RemoveNote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: string note } fe) return;
        if (FindAncestor<ListBox>(fe) is { } list && _lists.TryGetValue(list, out var source))
            ReturnToSuggestions(note, source);
        _dragItem = null;
        _dragSource = null;
        e.Handled = true;
    }

    // Delete/Backspace returns the selected note to Suggestions; Alt+Up/Down reorders it.
    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var list = (ListBox)sender;
        if (list.SelectedItem is not string note) return;
        var coll = _lists[list];

        if (e.Key is Key.Delete or Key.Back)
        {
            ReturnToSuggestions(note, coll);
            e.Handled = true;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down)
        {
            var i = coll.IndexOf(note);
            var j = key == Key.Up ? i - 1 : i + 1;
            if (i >= 0 && j >= 0 && j < coll.Count)
            {
                coll.Move(i, j);
                list.SelectedItem = note;
                list.ScrollIntoView(note);
            }
            e.Handled = true;
        }
    }

    // ── Drag and drop between (and within) lists ─────────────────────────────
    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        // A press on the ✕ button is a click, not the start of a drag.
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            _dragItem = null;
            _dragSource = null;
            return;
        }
        _dragItem = ContainerUnder(e.OriginalSource as DependencyObject)?.DataContext as string;
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
        _dragItem = null;
        _dragSource = null;
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _dragItem is not null ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void List_Drop(object sender, DragEventArgs e)
    {
        if (_dragItem is null || _dragSource is null) return;
        var list = (ListBox)sender;
        MoveNote(_dragItem, _dragSource, _lists[list], DropIndex(list, e));
        _dragItem = null;
        _dragSource = null;
        e.Handled = true;
    }

    // Insert position under the cursor: before the hovered row (upper half) or after it (lower half);
    // the end of the list when dropped on empty space.
    private static int DropIndex(ListBox list, DragEventArgs e)
    {
        var container = ContainerUnder(e.OriginalSource as DependencyObject);
        var idx = container is null ? -1 : list.ItemContainerGenerator.IndexFromContainer(container);
        if (container is null || idx < 0) return list.Items.Count;
        return e.GetPosition(container).Y > container.ActualHeight / 2 ? idx + 1 : idx;
    }

    // While dragging near the top/bottom edge of the sections panel, scroll it so every section is reachable.
    private void Buckets_PreviewDragOver(object sender, DragEventArgs e)
    {
        const double edge = 32, step = 14;
        var y = e.GetPosition(BucketsScroll).Y;
        if (y < edge) BucketsScroll.ScrollToVerticalOffset(BucketsScroll.VerticalOffset - step);
        else if (y > BucketsScroll.ActualHeight - edge) BucketsScroll.ScrollToVerticalOffset(BucketsScroll.VerticalOffset + step);
    }

    // A ListBox swallows the mouse wheel even when it has nothing to scroll (the section lists grow to fit
    // inside the outer panel), so hand the wheel to the enclosing ScrollViewer whenever the list can't use it.
    private void List_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var list = (ListBox)sender;
        var own = FindDescendant<ScrollViewer>(list);
        var canScrollSelf = own is not null && own.ScrollableHeight > 0 &&
                            (e.Delta > 0 ? own.VerticalOffset > 0 : own.VerticalOffset < own.ScrollableHeight);
        if (canScrollSelf) return;

        if (FindAncestor<ScrollViewer>(VisualTreeHelper.GetParent(list)) is not { } outer) return;
        e.Handled = true;
        outer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = list,
        });
    }

    private static ListBoxItem? ContainerUnder(DependencyObject? src) => FindAncestor<ListBoxItem>(src);

    private static T? FindAncestor<T>(DependencyObject? src) where T : DependencyObject
    {
        while (src is not null and not T)
            src = src is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(src)
                : LogicalTreeHelper.GetParent(src);
        return src as T;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
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
            StatusText.Text = S("Str.RNCB.NothingCategorized");
            return;
        }

        NotesBox.Text = sb.ToString().TrimEnd() + "\n";
        CopyBtn.IsEnabled = true;
        SaveBtn.IsEnabled = true;
        PublishBtn.IsEnabled = true;
        ChangelogBtn.IsEnabled = true;
        Tabs.SelectedIndex = 1;   // jump to the Draft tab
        StatusText.Text = S("Str.RNCB.DraftReady");
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
        try { Clipboard.SetText(NotesBox.Text); StatusText.Text = S("Str.RNCB.CopiedClipboard"); }
        catch (Exception ex) { StatusText.Text = S("Str.RNCB.CopyFailedFmt", ex.Message); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var version = VersionBox.Text.Trim();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = S("Str.RNCB.SaveTitle"),
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"release-notes-{(string.IsNullOrWhiteSpace(version) ? "draft" : version)}.md",
        };
        // Open the dialog in the chosen destination (the app folder or the solution folder).
        if (SaveDestBox.SelectedItem is string dest && _saveDestinations.TryGetValue(dest, out var folder)
            && Directory.Exists(folder))
            dlg.InitialDirectory = folder;

        if (dlg.ShowDialog() != true) return;
        try { File.WriteAllText(dlg.FileName, NotesBox.Text); StatusText.Text = S("Str.RNCB.SavedToFmt", dlg.FileName); }
        catch (Exception ex) { StatusText.Text = S("Str.RNCB.SaveFailedFmt", ex.Message); }
    }

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        // Prefer the Version field as the release tag; fall back to the "to" ref.
        var tag = VersionBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(tag)) tag = ResolveRef(ToCombo);
        if (string.IsNullOrWhiteSpace(tag))
        {
            StatusText.Text = S("Str.RNCB.SetVersionFirst");
            return;
        }

        var confirm = new ConfirmDialog(S("Str.RNCB.PublishConfirmTitle"),
            S("Str.RNCB.PublishConfirmMsgFmt", tag),
            S("Str.RNCB.PublishConfirmBtn"), isDanger: false) { Owner = this };
        if (confirm.ShowDialog() != true) return;

        Spinner.Visibility = Visibility.Visible;
        StatusText.Text = S("Str.RNCB.PublishingReleaseFmt", tag);
        PublishBtn.IsEnabled = false;
        try
        {
            await _github.PublishReleaseNotesAsync(_repo, _token, tag, NotesBox.Text);
            StatusText.Text = S("Str.RNCB.PublishedReleaseFmt", tag);
        }
        catch (Exception ex)
        {
            StatusText.Text = S("Str.RNCB.PublishFailedFmt", ex.Message);
        }
        finally
        {
            Spinner.Visibility = Visibility.Collapsed;
            PublishBtn.IsEnabled = true;
        }
    }

    private void AddToChangelog_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_appFolder) && string.IsNullOrWhiteSpace(_solutionFolder)) return;
        var section = NotesBox.Text.Trim();
        if (section.Length == 0) { StatusText.Text = S("Str.RNCB.BuildDraftFirst"); return; }

        try
        {
            var written = _changelog.WriteSection(_appFolder, _solutionFolder, section,
                VersionBox.Text.Trim(), _appName ?? string.Empty);
            ChangelogWritten = written.Count > 0;

            var where = written.Select(p =>
                !string.IsNullOrWhiteSpace(_solutionFolder) && IsUnder(p, _solutionFolder!) &&
                !(!string.IsNullOrWhiteSpace(_appFolder) && IsUnder(p, _appFolder!))
                    ? $"{S("Str.RNCB.SaveDestSolution")} ({Path.GetFileName(p)})"
                    : $"{S("Str.RNCB.SaveDestApp")} ({Path.GetFileName(p)})");
            StatusText.Text = S("Str.RNCB.AddedToFmt", string.Join(", ", where));
        }
        catch (Exception ex)
        {
            StatusText.Text = S("Str.RNCB.ChangelogFailedFmt", ex.Message);
        }
    }

    private static bool IsUnder(string path, string folder)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
