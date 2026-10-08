using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IndexExplorer.ViewModels;

public enum NodeKind { IndexRoot, IndexFolder, Inbox, AllFiles, ThisPC, Dir }

/// <summary>왼쪽 트리의 한 줄 (색인 폴더 또는 원본 폴더).</summary>
public sealed partial class TreeNode : ObservableObject
{
    public NodeKind Kind { get; init; }
    public long FolderId { get; init; }
    public string Path { get; init; } = "";
    public TreeNode? Parent { get; init; }
    public ObservableCollection<TreeNode> Children { get; } = [];

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    /// <summary>현재 보고 있는 폴더의 상위 → 가이드 선을 강조.</summary>
    [ObservableProperty] private bool _isOnPath;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private bool _isDropTarget;
    /// <summary>저장 위치가 직접 지정된 색인 폴더 (핀 표시).</summary>
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private string _badge = "";

    /// <summary>원본 폴더는 펼칠 때 하위를 읽는다.</summary>
    public bool ChildrenLoaded { get; set; }

    public bool IsIndex => Kind is NodeKind.IndexRoot or NodeKind.IndexFolder;
    public bool CanRename => Kind == NodeKind.IndexFolder || (Kind == NodeKind.Dir && Parent?.Kind != NodeKind.ThisPC && Parent != null);

    /// <summary>원본 폴더 하위 읽기 (펼침 화살표 표시를 위해 미리 한 단계만).</summary>
    public static readonly TreeNode Placeholder = new() { Name = "…" };

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && Kind == NodeKind.Dir && !ChildrenLoaded) LoadChildren();
    }

    public void LoadChildren()
    {
        ChildrenLoaded = true;
        Children.Clear();
        foreach (var d in SafeDirs(Path))
            Children.Add(CreateDir(d, this));
    }

    public static TreeNode CreateDir(string path, TreeNode? parent, string? name = null)
    {
        var n = new TreeNode
        {
            Kind = NodeKind.Dir, Path = path, Parent = parent,
            Name = name ?? System.IO.Path.GetFileName(path),
            Icon = Native.ShellImages.Icon(path, true),
        };
        if (HasSubDirs(path)) n.Children.Add(Placeholder);
        return n;
    }

    public static IEnumerable<string> SafeDirs(string path)
    {
        try
        {
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            return Directory.EnumerateDirectories(path, "*", opts).OrderBy(p => System.IO.Path.GetFileName(p), Services.NaturalComparer.Instance).ToList();
        }
        catch { return []; }
    }

    private static bool HasSubDirs(string path)
    {
        try
        {
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            return Directory.EnumerateDirectories(path, "*", opts).Any();
        }
        catch { return false; }
    }

    public IEnumerable<TreeNode> Ancestors()
    {
        for (var p = Parent; p != null; p = p.Parent) yield return p;
    }
}
