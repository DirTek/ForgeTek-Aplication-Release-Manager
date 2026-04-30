using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.ViewModels;

public enum DiffChange { None, Added, Modified, Removed, Unchanged }
public enum SortMode  { Name, Date }

public partial class FileTreeNodeViewModel : ObservableObject
{
    public string Name { get; }
    public string RelativePath { get; }
    public bool IsFolder { get; }
    public FileRecord? Record { get; }
    public DiffChange Change { get; }
    public DateTime? DateModified => Record?.DateModified;

    [ObservableProperty] private bool _isIncluded = true;
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
        _isIncluded = !record.IsDebug;
    }

    // File node for diff view
    public FileTreeNodeViewModel(FileRecord record, DiffChange change) : this(record)
    {
        Change = change;
    }

    partial void OnIsIncludedChanged(bool value)
    {
        foreach (var child in Children)
            child.IsIncluded = value;
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

    // ── Search filter ──────────────────────────────────────────────────────

    // Call with empty/null search to clear the filter (all visible, folders collapsed).
    public static void ApplySearch(IEnumerable<FileTreeNodeViewModel> nodes, string search)
    {
        bool active = !string.IsNullOrEmpty(search);
        ApplySearchRecursive(nodes, search, active);
    }

    private static bool ApplySearchRecursive(IEnumerable<FileTreeNodeViewModel> nodes, string search, bool active)
    {
        bool anyVisible = false;
        foreach (var node in nodes)
        {
            if (node.IsFolder)
            {
                bool childHit = ApplySearchRecursive(node.Children, search, active);
                node.IsVisible  = !active || childHit;
                node.IsExpanded = active && childHit;
                anyVisible |= node.IsVisible;
            }
            else
            {
                node.IsVisible = !active ||
                    node.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase);
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
                node.Record.IsDebug = !node.IsIncluded;
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
