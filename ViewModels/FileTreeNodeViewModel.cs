using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.ViewModels;

public enum DiffChange  { None, Added, Modified, Removed, Unchanged }
public enum SortMode   { Name, Date }
public enum DebugFilter { All, NonDebug, DebugOnly }

public partial class FileTreeNodeViewModel : ObservableObject
{
    public string Name { get; }
    public string RelativePath { get; }
    public bool IsFolder { get; }
    public FileRecord? Record { get; }
    public DiffChange Change { get; }
    public DateTime? DateModified => Record?.DateModified;

    [ObservableProperty] private bool _isIncluded = true;
    [ObservableProperty] private bool _isDebug    = false;
    // Marked for deletion on clients (not shipped, added to the package's RemovedFiles).
    [ObservableProperty] private bool _isRemoved  = false;
    [ObservableProperty] private bool _isExpanded = false;
    [ObservableProperty] private bool _isVisible  = true;

    public ObservableCollection<FileTreeNodeViewModel> Children { get; } = [];

    // Folder node
    public FileTreeNodeViewModel(string name, string relativePath)
    {
        Name = name;
        RelativePath = relativePath;
        IsFolder = true;
    }

    // File node for scan view
    public FileTreeNodeViewModel(FileRecord record)
    {
        var p = record.Path;
        var lastSep = p.LastIndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        Name = lastSep < 0 ? p : p[(lastSep + 1)..];
        RelativePath = record.Path;
        IsFolder = false;
        Record = record;
        _isIncluded = !record.IsDebug && !record.IsRemoved;
        _isDebug    = record.IsDebug;
        _isRemoved  = record.IsRemoved;
    }

    // File node for diff view
    public FileTreeNodeViewModel(FileRecord record, DiffChange change) : this(record)
    {
        Change = change;
    }

    // "Include" is the single user-facing toggle; it's the inverse of the persisted debug/exclude
    // flag. Folders cascade to their children; files mirror to IsDebug (→ Record.IsDebug).
    // The two setters converge because assigning an unchanged value doesn't re-raise the change.
    partial void OnIsIncludedChanged(bool value)
    {
        if (IsFolder)
        {
            foreach (var child in Children)
                child.IsIncluded = value;
        }
        else
        {
            if (value && IsRemoved) IsRemoved = false;   // re-including clears a pending removal
            IsDebug = !value;
        }
    }

    partial void OnIsDebugChanged(bool value)
    {
        if (!value && IsRemoved) return;   // a removed file stays excluded from shipping
        IsIncluded = !value;
        if (Record is not null) Record.IsDebug = value;
    }

    partial void OnIsRemovedChanged(bool value)
    {
        if (Record is not null) Record.IsRemoved = value;
        if (value) IsIncluded = false;     // marked for deletion → never shipped
    }

    /// <summary>Toggles "delete on clients" for a file node (cascades to a folder's files).</summary>
    [RelayCommand]
    private void ToggleRemove()
    {
        if (IsFolder)
            foreach (var child in Children) child.ToggleRemoveCommand.Execute(null);
        else
            IsRemoved = !IsRemoved;
    }

    // ── Build ─────────────────────────────────────────────────────────────

    public static ObservableCollection<FileTreeNodeViewModel> BuildScanTree(
        IReadOnlyList<FileRecord> files, SortMode sortMode = SortMode.Name)
    {
        var roots = new ObservableCollection<FileTreeNodeViewModel>();
        PopulateTree(roots, files, f => new FileTreeNodeViewModel(f));
        SortNodes(roots, sortMode);
        return roots;
    }

    public static ObservableCollection<FileTreeNodeViewModel> BuildDiffTree(
        IEnumerable<FileRecord> files, DiffChange change)
    {
        var list = files.ToList();
        var roots = new ObservableCollection<FileTreeNodeViewModel>();
        PopulateTree(roots, list, f => new FileTreeNodeViewModel(f, change));
        SortNodes(roots, SortMode.Name);
        return roots;
    }

    // ── Search + debug filter ─────────────────────────────────────────────

    // Combines a text search with a DebugFilter in a single tree traversal.
    // Call with empty search and DebugFilter.All to reset visibility (all visible, folders collapsed).
    public static void ApplyFilters(
        IEnumerable<FileTreeNodeViewModel> nodes,
        string search,
        DebugFilter debugFilter = DebugFilter.All)
    {
        bool searchActive = !string.IsNullOrEmpty(search);
        bool filterActive = debugFilter != DebugFilter.All;
        ApplyFiltersRecursive(nodes, search, searchActive, debugFilter, searchActive || filterActive);
    }

    // Backward-compat wrapper for callers that only need text search.
    public static void ApplySearch(IEnumerable<FileTreeNodeViewModel> nodes, string search)
        => ApplyFilters(nodes, search, DebugFilter.All);

    private static bool ApplyFiltersRecursive(
        IEnumerable<FileTreeNodeViewModel> nodes,
        string search,
        bool searchActive,
        DebugFilter debugFilter,
        bool anyActive)
    {
        bool anyVisible = false;
        foreach (var node in nodes)
        {
            if (node.IsFolder)
            {
                bool childHit = ApplyFiltersRecursive(node.Children, search, searchActive, debugFilter, anyActive);
                node.IsVisible  = !anyActive || childHit;
                node.IsExpanded = anyActive && childHit;
                anyVisible |= node.IsVisible;
            }
            else
            {
                bool passesDebug = debugFilter switch
                {
                    DebugFilter.NonDebug  => !node.IsDebug,
                    DebugFilter.DebugOnly =>  node.IsDebug,
                    _                     =>  true,
                };
                bool passesSearch = !searchActive ||
                    node.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase);

                node.IsVisible = passesDebug && passesSearch;
                anyVisible |= node.IsVisible;
            }
        }
        return anyVisible;
    }

    // ── Collect for save ───────────────────────────────────────────────────

    public static IEnumerable<FileRecord> CollectFiles(IEnumerable<FileTreeNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsFolder)
            {
                foreach (var f in CollectFiles(node.Children))
                    yield return f;
            }
            else if (node.Record is not null)
            {
                node.Record.IsDebug = node.IsDebug;
                node.Record.IsRemoved = node.IsRemoved;
                yield return node.Record;
            }
        }
    }

    // ── Internals ──────────────────────────────────────────────────────────

    private static void PopulateTree(
        ObservableCollection<FileTreeNodeViewModel> roots,
        IReadOnlyList<FileRecord> files,
        Func<FileRecord, FileTreeNodeViewModel> fileFactory)
    {
        var folderIndex = new Dictionary<string, FileTreeNodeViewModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var parts = file.Path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            var currentLevel = roots;

            for (int i = 0; i < parts.Length - 1; i++)
            {
                var folderPath = string.Join(Path.DirectorySeparatorChar, parts[..(i + 1)]);
                if (!folderIndex.TryGetValue(folderPath, out var folder))
                {
                    folder = new FileTreeNodeViewModel(parts[i], folderPath);
                    folderIndex[folderPath] = folder;
                    currentLevel.Add(folder);
                }
                currentLevel = folder.Children;
            }

            currentLevel.Add(fileFactory(file));
        }
    }

    private static void SortNodes(ObservableCollection<FileTreeNodeViewModel> nodes, SortMode sortMode)
    {
        List<FileTreeNodeViewModel> sorted;
        if (sortMode == SortMode.Date)
        {
            sorted = nodes
                .OrderBy(n => n.IsFolder ? 0 : 1)
                .ThenBy(n => n.IsFolder ? n.Name : string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(n => n.DateModified ?? DateTime.MinValue)
                .ToList();
        }
        else
        {
            sorted = nodes
                .OrderBy(n => n.IsFolder ? 0 : 1)
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        nodes.Clear();
        foreach (var node in sorted)
        {
            SortNodes(node.Children, sortMode);
            nodes.Add(node);
        }
    }
}
