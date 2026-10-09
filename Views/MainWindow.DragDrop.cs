using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IndexExplorer.Native;
using IndexExplorer.Services;
using IndexExplorer.ViewModels;

namespace IndexExplorer.Views;

/// <summary>
/// 끌어다 놓기 규칙
/// - 색인 폴더 → 다른 색인 폴더: 옮기기 (Ctrl: 양쪽 모두에 연결)
/// - 원본·미분류 함·탐색기 → 색인 폴더: 연결 추가 (파일은 그대로)
/// - 색인 폴더 → 미분류 함: 그 색인에서 빼기
/// - 원본 폴더로 놓기: 탐색기와 같은 실제 복사/이동 (같은 드라이브는 이동, Ctrl 복사, Shift 이동)
/// - 바깥(탐색기·Outlook 등)으로 끌기: 실제 파일이 전달됨 (메일 첨부 등)
/// </summary>
public partial class MainWindow
{
    private const string DragFormat = "IndexExplorer.Drag";

    private sealed record DragPayload(List<string> Paths, List<long> FileIds, List<long> FolderIds, long? SourceFolder);

    private enum DropKind { None, IndexFolder, IndexRoot, Inbox, Dir }

    private sealed record DropTarget(DropKind Kind, long FolderId = 0, string Path = "", TreeNode? Node = null, ListItem? Item = null);

    private static DragPayload? _drag; // 다른 창으로 끌어다 놓아도 앱 안의 동작으로 처리
    private Point _dragStart;
    private object? _dragArmedOn;
    private ListViewItem? _pendingSingleSelect;
    private TreeNode? _hoverNode;
    private DateTime _hoverSince;
    private TreeNode? _litNode;
    private ListItem? _litItem;

    // ===================== 끌기 시작 =====================

    private void List_PreviewLeftDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragArmedOn = null;
        _pendingSingleSelect = null;
        _rowClick = null;
        var src = e.OriginalSource as DependencyObject;
        if (FindAncestor<TextBox>(src) != null) return;
        if (FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(src) != null || FindAncestor<GridViewColumnHeader>(src) != null) return;
        var container = FindAncestor<ListViewItem>(src);
        if (container == null)
        {
            // 빈 곳: 선택 해제 (탐색기와 같음), 그대로 끌면 범위 선택
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0) List.SelectedItems.Clear();
            List.Focus();
            if (ArmMarquee(e)) e.Handled = true;
            return;
        }
        if (List.View != null && !IsOnNameContent(src, container))
        {
            // 자세히 보기에서 이름 글자 밖: 누르면 그 줄 선택, 끌면 범위 선택
            if (e.ClickCount == 2 && container.DataContext is ListItem li) { Open([li]); e.Handled = true; return; }
            if (ArmMarquee(e))
            {
                _rowClick = container;
                container.Focus();
                e.Handled = true;
                return;
            }
        }
        _dragArmedOn = container;
        // 여러 개를 선택한 상태에서 그중 하나를 누르면, 끌기를 위해 선택을 유지한다
        if (container.IsSelected && List.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _pendingSingleSelect = container;
            container.Focus();
            e.Handled = true;
        }
    }

    private void List_PreviewLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_marqueeArmed)
        {
            var wasActive = _marqueeActive;
            var row = _rowClick;
            EndMarquee();
            _rowClick = null;
            if (!wasActive && row != null) RowClickSelect(row);
            return;
        }
        if (_pendingSingleSelect is { DataContext: ListItem item })
        {
            List.SelectedItems.Clear();
            List.SelectedItems.Add(item);
        }
        _pendingSingleSelect = null;
        _dragArmedOn = null;
    }

    private bool DragMoved(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        return Math.Abs(p.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance
               || Math.Abs(p.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance;
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_marqueeArmed)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { EndMarquee(); return; }
            if (!_marqueeActive && DragMoved(e)) StartMarquee();
            if (_marqueeActive) UpdateMarquee();
            return;
        }
        if (_dragArmedOn == null || e.LeftButton != MouseButtonState.Pressed || !DragMoved(e)) return;
        _dragArmedOn = null;
        _pendingSingleSelect = null;
        var sel = Selected();
        if (sel.Count == 0 || sel.Any(i => i.IsEditing || i.Kind == ItemKind.Drive)) return;
        var paths = sel.Where(i => i.Kind is ItemKind.File or ItemKind.Dir && !i.Missing).Select(i => i.Path).ToList();
        StartDrag(List, new DragPayload(paths,
            sel.Select(i => i.Record?.Id).OfType<long>().ToList(),
            sel.Where(i => i.Kind == ItemKind.IndexFolder).Select(i => i.FolderId).ToList(),
            _loc.Kind == LocKind.IndexFolder ? _loc.FolderId : null));
    }

    private TreeNode? _treeDragNode;

    private void Tree_PreviewLeftDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        var tvi = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        _treeDragNode = FindAncestor<TextBox>(e.OriginalSource as DependencyObject) == null ? tvi?.DataContext as TreeNode : null;
        // 이미 선택된 항목을 다시 눌러도 그 위치로 돌아간다 (검색 중 등)
        if (_treeDragNode is { IsSelected: true } n && FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(e.OriginalSource as DependencyObject) == null)
        {
            var target = LocationOf(n);
            if (!_loc.Same(target)) Dispatcher.BeginInvoke(() => Navigate(target));
        }
    }

    private void Tree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_treeDragNode == null || e.LeftButton != MouseButtonState.Pressed || !DragMoved(e)) return;
        var node = _treeDragNode;
        _treeDragNode = null;
        if (node.Kind == NodeKind.IndexFolder)
            StartDrag((UIElement)sender, new DragPayload([], [], [node.FolderId], null));
        else if (node.Kind == NodeKind.Dir && node.Parent is { Kind: not NodeKind.ThisPC })
            StartDrag((UIElement)sender, new DragPayload([node.Path], [], [], null));
    }

    private void StartDrag(UIElement source, DragPayload payload)
    {
        var data = new DataObject();
        if (payload.Paths.Count > 0)
        {
            var sc = new System.Collections.Specialized.StringCollection();
            sc.AddRange(payload.Paths.ToArray());
            data.SetFileDropList(sc);
        }
        data.SetData(DragFormat, "1");
        _drag = payload;
        try
        {
            DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch (Exception ex) { App.Log("drag: " + ex.Message); }
        finally
        {
            _drag = null;
            ClearDropHighlight();
            RefreshNow();
        }
    }

    // ===================== 놓을 곳 판단 =====================

    private DragPayload? PayloadOf(IDataObject data) => data.GetDataPresent(DragFormat) ? _drag : null;

    private static string[] FilesOf(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] ?? [] : [];

    private static DropTarget TargetOfNode(TreeNode? n) => n?.Kind switch
    {
        NodeKind.IndexFolder => new DropTarget(DropKind.IndexFolder, n.FolderId, Node: n),
        NodeKind.IndexRoot => new DropTarget(DropKind.IndexRoot, Node: n),
        NodeKind.Inbox => new DropTarget(DropKind.Inbox, Node: n),
        NodeKind.Dir => new DropTarget(DropKind.Dir, Path: n.Path, Node: n),
        _ => new DropTarget(DropKind.None),
    };

    private DropTarget TargetOfList(DragEventArgs e)
    {
        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext as ListItem;
        switch (item?.Kind)
        {
            case ItemKind.IndexFolder: return new DropTarget(DropKind.IndexFolder, item.FolderId, Item: item);
            case ItemKind.Dir or ItemKind.Drive: return new DropTarget(DropKind.Dir, Path: item.Path, Item: item);
        }
        return _loc.Kind switch
        {
            LocKind.IndexFolder => new DropTarget(DropKind.IndexFolder, _loc.FolderId),
            LocKind.IndexRoot => new DropTarget(DropKind.IndexRoot),
            LocKind.Inbox => new DropTarget(DropKind.Inbox),
            LocKind.Dir => new DropTarget(DropKind.Dir, Path: _loc.Path),
            _ => new DropTarget(DropKind.None),
        };
    }

    private DragDropEffects Decide(DropTarget t, DragEventArgs e)
    {
        var payload = PayloadOf(e.Data);
        var files = FilesOf(e.Data);
        bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        bool shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
        var effect = DragDropEffects.None;

        switch (t.Kind)
        {
            case DropKind.IndexFolder:
                if (payload != null)
                {
                    if (payload.FolderIds.Count > 0)
                        effect = payload.FolderIds.Any(id => id == t.FolderId || _store.IsAncestor(id, t.FolderId)) ? DragDropEffects.None : DragDropEffects.Move;
                    else if (payload.SourceFolder is long src)
                        effect = src == t.FolderId ? DragDropEffects.None : ctrl ? DragDropEffects.Link : DragDropEffects.Move;
                    else if (payload.Paths.Count > 0 || payload.FileIds.Count > 0)
                        effect = DragDropEffects.Link;
                }
                else if (files.Length > 0) effect = DragDropEffects.Link;
                break;

            case DropKind.IndexRoot:
                if (payload is { FolderIds.Count: > 0 } && payload.FolderIds.All(id => _store.Folder(id)?.ParentId != null))
                    effect = DragDropEffects.Move;
                break;

            case DropKind.Inbox:
                if (payload is { SourceFolder: not null, FileIds.Count: > 0 }) effect = DragDropEffects.Move;
                break;

            case DropKind.Dir:
                if (files.Length == 0) break;
                // 자기 자신이나 자기 하위 폴더, 이미 그 폴더에 있는 파일 위로는 놓을 수 없다
                if (files.Any(f => PathUtil.IsUnder(t.Path, f) || PathUtil.Dir(f).Equals(Path.TrimEndingDirectorySeparator(t.Path), StringComparison.OrdinalIgnoreCase))) break;
                if (ctrl) effect = DragDropEffects.Copy;
                else if (shift) effect = DragDropEffects.Move;
                else if (payload?.SourceFolder != null) effect = DragDropEffects.Copy; // 색인에서 끌어온 파일은 원본을 지키기 위해 복사
                else effect = SameVolume(files[0], t.Path) ? DragDropEffects.Move : DragDropEffects.Copy;
                break;
        }
        if (effect != DragDropEffects.None && (e.AllowedEffects & effect) == 0)
            effect = (e.AllowedEffects & DragDropEffects.Copy) != 0 && t.Kind == DropKind.Dir ? DragDropEffects.Copy
                : (e.AllowedEffects & DragDropEffects.Link) != 0 && t.Kind == DropKind.IndexFolder ? DragDropEffects.Link
                : DragDropEffects.None;
        return effect;
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    private void Highlight(DropTarget t, bool on)
    {
        if (on && t.Node == _litNode && t.Item == _litItem) return;
        ClearDropHighlight();
        if (!on) return;
        _litNode = t.Node;
        _litItem = t.Item;
        if (_litNode != null) _litNode.IsDropTarget = true;
        if (_litItem != null) _litItem.IsDropTarget = true;
    }

    private void ClearDropHighlight()
    {
        if (_litNode != null) _litNode.IsDropTarget = false;
        if (_litItem != null) _litItem.IsDropTarget = false;
        _litNode = null;
        _litItem = null;
    }

    // ===================== 트리에 놓기 =====================

    private void Tree_DragOver(object sender, DragEventArgs e)
    {
        var node = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as TreeNode;
        var t = TargetOfNode(node);
        e.Effects = Decide(t, e);
        Highlight(t, e.Effects != DragDropEffects.None);

        // 잠시 머무르면 하위 폴더를 펼친다
        if (node != _hoverNode) { _hoverNode = node; _hoverSince = DateTime.Now; }
        else if (node is { IsExpanded: false } && node.Children.Count > 0 && (DateTime.Now - _hoverSince).TotalMilliseconds > 700)
            node.IsExpanded = true;
        e.Handled = true;
    }

    private void Tree_DragLeave(object sender, DragEventArgs e) => ClearDropHighlight();

    private void Tree_Drop(object sender, DragEventArgs e)
    {
        var node = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as TreeNode;
        var t = TargetOfNode(node);
        e.Effects = Decide(t, e);
        ClearDropHighlight();
        if (e.Effects != DragDropEffects.None) Execute(t, e.Effects, e.Data);
        e.Handled = true;
    }

    // ===================== 목록에 놓기 =====================

    private void List_DragOver(object sender, DragEventArgs e)
    {
        var t = TargetOfList(e);
        e.Effects = Decide(t, e);
        Highlight(t, e.Effects != DragDropEffects.None && (t.Item != null));
        if (t.Item == null && _litItem != null) ClearDropHighlight();
        e.Handled = true;
    }

    private void List_DragLeave(object sender, DragEventArgs e) => ClearDropHighlight();

    private void List_Drop(object sender, DragEventArgs e)
    {
        var t = TargetOfList(e);
        e.Effects = Decide(t, e);
        ClearDropHighlight();
        if (e.Effects != DragDropEffects.None) Execute(t, e.Effects, e.Data);
        e.Handled = true;
    }

    // ===================== 실행 =====================

    private void Execute(DropTarget t, DragDropEffects effect, IDataObject data)
    {
        var payload = PayloadOf(data);
        var files = FilesOf(data);
        // 끌기를 시작한 쪽의 모달 루프가 끝난 뒤 처리 (셸 복사 창이 끌기 중에 뜨지 않도록)
        Dispatcher.BeginInvoke(() =>
        {
            switch (t.Kind)
            {
                case DropKind.IndexFolder:
                    if (payload is { FolderIds.Count: > 0 })
                        MoveIndexFolders(payload.FolderIds, t.FolderId);
                    else if (payload is { SourceFolder: long src } && effect == DragDropEffects.Move)
                        MoveLinks(src, t.FolderId, payload.FileIds);
                    else
                    {
                        var paths = payload?.Paths ?? files.ToList();
                        var n = LinkPaths(t.FolderId, paths);
                        if (payload != null)
                            foreach (var id in payload.FileIds)
                                if (_store.File(id) is { Missing: true } m) { _store.Link(t.FolderId, m); n++; }
                        RefreshNow();
                        Toast($"{n}개 항목을 ‘{FolderName(t.FolderId)}’ 색인에 연결했습니다", "원본 파일은 그대로 있습니다.",
                            "열기", () => Navigate(new Location(LocKind.IndexFolder, t.FolderId)));
                    }
                    break;

                case DropKind.IndexRoot:
                    if (payload != null) MoveIndexFolders(payload.FolderIds, null);
                    break;

                case DropKind.Inbox:
                    if (payload is { SourceFolder: long from })
                    {
                        var recs = payload.FileIds.Select(_store.File).OfType<FileRecord>().ToList();
                        _store.Batch(() => { foreach (var r in recs) _store.Unlink(from, r); });
                        RefreshNow();
                        Toast($"{recs.Count}개 항목을 ‘{FolderName(from)}’ 색인에서 뺐습니다", "원본 파일은 그대로 있습니다.", "실행 취소", () =>
                        {
                            _store.Batch(() => { foreach (var r in recs) if (_store.File(r.Id) != null) _store.Link(from, r); });
                            RefreshNow();
                        });
                    }
                    break;

                case DropKind.Dir:
                    if (files.Length > 0)
                    {
                        ShellFileOps.TransferInto(this, effect == DragDropEffects.Move, files, t.Path);
                        if (t.Node is { ChildrenLoaded: true } node) node.LoadChildren();
                        RefreshNow();
                    }
                    break;
            }
        });
    }
}
