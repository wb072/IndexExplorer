using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IndexExplorer.Native;
using IndexExplorer.Services;
using IndexExplorer.ViewModels;
using Microsoft.Win32;

namespace IndexExplorer.Views;

public partial class MainWindow
{
    private const string ClipFormat = "IndexExplorer.Clip";

    /// <summary>앱 안에서 복사/잘라내기한 내용 (탐색기와 주고받는 FileDrop 과 함께 쓴다).</summary>
    private sealed record ClipState(string Token, List<string> Paths, List<long> FileIds, List<long> FolderIds, long? SourceFolder, bool Cut);

    private static ClipState? _clip; // 창끼리 공유

    private (string Dir, IndexFolder? PinnedBy) StorageOf(long folderId) => _store.StorageDir(folderId, _settings.StorageRoot);

    private string FolderName(long id) => _store.Folder(id)?.Name ?? "";

    // ===================== 열기 =====================

    private void OpenSelected() => Open(Selected());

    private void Open(IReadOnlyList<ListItem> items)
    {
        if (items.Count == 0) return;
        var folder = items.FirstOrDefault(i => i.IsFolderLike);
        foreach (var f in items.Where(i => i.Kind == ItemKind.File).Take(15))
        {
            if (f.Missing || !File.Exists(f.Path))
            {
                Toast("원본 파일을 찾을 수 없습니다", f.Path);
                continue;
            }
            ShellOpen(f.Path);
        }
        if (folder != null)
            Navigate(folder.Kind == ItemKind.IndexFolder ? new Location(LocKind.IndexFolder, folder.FolderId) : new Location(LocKind.Dir, Path: folder.Path));
    }

    private void ShellOpen(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception)
        {
            // 연결된 프로그램이 없으면 탐색기처럼 "연결 프로그램 선택" 창
            try { Process.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}"); } catch { }
        }
        catch (Exception ex) { Toast("파일을 열 수 없습니다", ex.Message); }
    }

    private void RevealOriginal(ListItem item)
    {
        var dir = PathUtil.Dir(item.Path);
        if (!Directory.Exists(dir)) { Toast("원본 폴더를 찾을 수 없습니다", dir); return; }
        Navigate(new Location(LocKind.Dir, Path: dir), selectPath: item.Path);
    }

    // ===================== 새로 만들기 =====================

    private void NewMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        FillNewMenu(menu.Items);
        OpenMenuUnder(menu, (FrameworkElement)sender);
    }

    private void FillNewMenu(ItemCollection items)
    {
        var folder = new MenuItem { Header = "폴더", InputGestureText = "Ctrl+Shift+N", Icon = MenuIcon(ShellImages.Icon(Settings.DataDir, true)) };
        folder.Click += (_, _) => NewFolder();
        items.Add(folder);
        items.Add(new Separator());
        foreach (var it in ShellNew.Items())
        {
            var mi = new MenuItem { Header = it.DisplayName, Icon = MenuIcon(ShellImages.Icon("x" + it.Extension, false)) };
            mi.Click += (_, _) => NewDocument(it);
            items.Add(mi);
        }
    }

    private static Image? MenuIcon(ImageSource? src) => src == null ? null : new Image { Source = src, Width = 16, Height = 16 };

    private void NewFolder()
    {
        switch (_loc.Kind)
        {
            case LocKind.IndexRoot or LocKind.IndexFolder:
                var parent = _loc.Kind == LocKind.IndexFolder ? _loc.FolderId : (long?)null;
                var f = _store.CreateFolder(parent);
                RefreshNow();
                SelectKeys(["F" + f.Id]);
                BeginRename();
                break;
            case LocKind.Dir:
                try
                {
                    var path = ShellFileOps.UniquePath(_loc.Path, "새 폴더", isDir: true);
                    Directory.CreateDirectory(path);
                    LoadItems(reset: false);
                    SelectPaths([path]);
                    BeginRename();
                }
                catch (Exception ex) { Toast("폴더를 만들 수 없습니다", ex.Message); }
                break;
            default:
                Toast("여기에는 폴더를 만들 수 없습니다", "색인 폴더나 원본 폴더를 연 뒤 다시 시도하세요.");
                break;
        }
    }

    private void NewDocument(ShellNewItem it)
    {
        string dir;
        IndexFolder? pinned = null;
        switch (_loc.Kind)
        {
            case LocKind.IndexFolder: (dir, pinned) = StorageOf(_loc.FolderId); break;
            case LocKind.Dir: dir = _loc.Path; break;
            case LocKind.IndexRoot:
                Toast("색인 폴더를 먼저 여세요", "파일은 색인 폴더 안에 만듭니다.");
                return;
            default:
                Toast("여기에는 문서를 만들 수 없습니다", "색인 폴더나 원본 폴더를 연 뒤 다시 시도하세요.");
                return;
        }
        try
        {
            Directory.CreateDirectory(dir);
            var path = ShellFileOps.UniquePath(dir, $"새 {it.DisplayName}{it.Extension}");
            it.Create(path);
            _indexer.EnsureWatching();
            if (_loc.Kind == LocKind.IndexFolder && _store.UpsertFromDisk(path) is { } rec)
            {
                _store.Link(_loc.FolderId, rec);
                Toast("새 문서를 만들었습니다", StorageNote(dir, pinned));
            }
            RefreshNow();
            SelectPaths([path]);
            BeginRename();
        }
        catch (Exception ex) { Toast("문서를 만들 수 없습니다", ex.Message); }
    }

    private string StorageNote(string dir, IndexFolder? pinned) => pinned == null
        ? $"원본 위치: {dir}\n저장 위치를 지정하지 않아 원본 보관함에 색인과 같은 구조로 저장했습니다."
        : $"원본 위치: {dir}  (‘{pinned.Name}’에 지정한 저장 위치)";

    /// <summary>색인이 바뀐 직후 기다리지 않고 바로 반영.</summary>
    private void RefreshNow()
    {
        _refreshTimer.Stop();
        if (FolderSignature() != _folderSignature) { BuildIndexTree(); SyncTreeSelection(); }
        else UpdateBadges();
        LoadItems(reset: false);
    }

    // ===================== 이름 바꾸기 =====================

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (IndexTree.IsKeyboardFocusWithin || PhysTree.IsKeyboardFocusWithin) RenameTreeNode();
        else BeginRename();
    }

    private void BeginRename()
    {
        var sel = Selected();
        if (sel.Count != 1) return;
        var item = sel[0];
        if (item.Kind == ItemKind.Drive || item.Missing) return;
        CancelAllEdits();
        List.ScrollIntoView(item);
        item.EditName = item.Name;
        item.IsEditing = true;
    }

    private void RenameTreeNode(TreeNode? node = null)
    {
        node ??= (IndexTree.IsKeyboardFocusWithin ? IndexTree.SelectedItem : PhysTree.SelectedItem) as TreeNode;
        if (node is not { CanRename: true }) return;
        CancelAllEdits();
        node.EditName = node.Name;
        node.IsEditing = true;
    }

    private void CancelAllEdits()
    {
        foreach (var i in _items.Where(i => i.IsEditing)) i.IsEditing = false;
        foreach (var n in AllNodes(_indexNodes).Concat(AllNodes(_physNodes)).Where(n => n.IsEditing)) n.IsEditing = false;
    }

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox tb || e.NewValue is not true) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            tb.Focus();
            // 파일은 확장자를 뺀 이름만 선택 (탐색기와 같음)
            if (tb.DataContext is ListItem { Kind: ItemKind.File } && Path.GetExtension(tb.Text).Length > 0)
                tb.Select(0, tb.Text.Length - Path.GetExtension(tb.Text).Length);
            else tb.SelectAll();
        });
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (e.Key == Key.Enter) { CommitRename(tb.DataContext); e.Handled = true; }
        else if (e.Key == Key.Escape) { CancelRename(tb.DataContext); e.Handled = true; }
    }

    private void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: var dc }) CommitRename(dc);
    }

    private void CancelRename(object dc)
    {
        if (dc is ListItem li) { li.IsEditing = false; FocusItem(li); }
        else if (dc is TreeNode n) n.IsEditing = false;
    }

    private bool _committing;

    private void CommitRename(object dc)
    {
        if (_committing) return;
        _committing = true;
        try
        {
            if (dc is ListItem { IsEditing: true } li)
            {
                li.IsEditing = false;
                var name = li.EditName.Trim();
                if (name.Length == 0 || name == li.Name) { FocusItem(li); return; }
                switch (li.Kind)
                {
                    case ItemKind.IndexFolder:
                        if (RenameIndexFolder(li.FolderId, name)) { RefreshNow(); SelectKeys(["F" + li.FolderId]); }
                        break;
                    case ItemKind.Dir or ItemKind.File:
                        var newPath = RenamePhysical(li.Path, name, li.Kind == ItemKind.Dir);
                        if (newPath != null) { RefreshNow(); SelectPaths([newPath]); }
                        else FocusItem(li);
                        break;
                }
            }
            else if (dc is TreeNode { IsEditing: true } n)
            {
                n.IsEditing = false;
                var name = n.EditName.Trim();
                if (name.Length == 0 || name == n.Name) return;
                if (n.Kind == NodeKind.IndexFolder)
                {
                    if (RenameIndexFolder(n.FolderId, name)) RefreshNow();
                }
                else if (n.Kind == NodeKind.Dir && RenamePhysical(n.Path, name, true) is { } newPath)
                {
                    if (n.Parent != null) { n.Parent.LoadChildren(); n.Parent.IsExpanded = true; }
                    if (PathUtil.IsUnder(_loc.Path, n.Path) && _loc.Kind == LocKind.Dir)
                        Navigate(new Location(LocKind.Dir, Path: newPath + _loc.Path[n.Path.Length..]), record: false);
                    else RefreshNow();
                }
            }
        }
        finally { _committing = false; }
    }

    private bool RenameIndexFolder(long id, string name)
    {
        var f = _store.Folder(id);
        if (f == null) return false;
        if (_store.NameTaken(f.ParentId, name, id))
        {
            Toast($"‘{name}’ 색인 폴더가 이미 있습니다", "다른 이름을 입력하세요.");
            return false;
        }
        FollowStorage(id, () => _store.RenameFolder(id, name));
        return true;
    }

    /// <returns>바뀐 경로, 실패하면 null</returns>
    private string? RenamePhysical(string path, string name, bool isDir)
    {
        if (!ShellFileOps.IsValidName(name))
        {
            Toast("이름에 쓸 수 없는 글자가 있습니다", "\\ / : * ? \" < > | 는 쓸 수 없습니다.");
            return null;
        }
        var newPath = Path.Combine(PathUtil.Dir(path), name);
        if (!isDir && !Path.GetExtension(path).Equals(Path.GetExtension(name), StringComparison.OrdinalIgnoreCase))
        {
            var r = MessageBox.Show(this, "확장자를 변경하면 파일을 사용할 수 없게 될 수도 있습니다.\n\n변경하시겠습니까?", "이름 바꾸기",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return null;
        }
        try
        {
            var caseOnly = path.Equals(newPath, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(newPath) || Directory.Exists(newPath)))
            {
                Toast($"‘{name}’이(가) 이미 있습니다", "다른 이름을 입력하세요.");
                return null;
            }
            if (isDir)
            {
                if (caseOnly) { var tmp = path + ".~rn"; Directory.Move(path, tmp); Directory.Move(tmp, newPath); }
                else Directory.Move(path, newPath);
                _store.RebaseDir(path, newPath);
            }
            else
            {
                File.Move(path, newPath);
                if (_store.ByPath(path) is { } rec) _store.UpdatePath(rec, newPath);
            }
            return newPath;
        }
        catch (Exception ex)
        {
            Toast("이름을 바꿀 수 없습니다", ex is IOException ? "다른 프로그램에서 사용 중일 수 있습니다." : ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 색인 폴더 이름 변경·이동 시, 원본 보관함 안의 같은 구조 폴더도 함께 맞춘다
    /// (원본 보관함 밖의 파일은 절대 건드리지 않는다).
    /// </summary>
    private void FollowStorage(long folderId, Action change)
    {
        var oldDir = StorageOf(folderId).Dir;
        change();
        var newDir = StorageOf(folderId).Dir;
        if (oldDir.Equals(newDir, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(oldDir) || Directory.Exists(newDir)) return;
        if (!PathUtil.IsUnder(oldDir, _settings.StorageRoot) || !PathUtil.IsUnder(newDir, _settings.StorageRoot)) return;
        try
        {
            Directory.CreateDirectory(PathUtil.Dir(newDir));
            Directory.Move(oldDir, newDir);
            _store.RebaseDir(oldDir, newDir);
        }
        catch (Exception ex) { Toast("원본 보관함의 폴더를 맞추지 못했습니다", ex.Message); }
    }

    // ===================== 삭제 =====================

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (IndexTree.IsKeyboardFocusWithin || PhysTree.IsKeyboardFocusWithin) DeleteTreeNode();
        else DeleteSelected(shift: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    /// <summary>
    /// 색인 폴더 안: Delete = 이 색인에서 빼기 (원본 그대로), Shift+Delete = 원본을 휴지통으로.
    /// 그 밖(원본·미분류 함·검색): 원본을 휴지통으로. 영구 삭제는 하지 않는다.
    /// </summary>
    private void DeleteSelected(bool shift)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var folders = sel.Where(i => i.Kind == ItemKind.IndexFolder).Select(i => i.FolderId).ToList();
        var files = sel.Where(i => i.Kind is ItemKind.File or ItemKind.Dir).ToList();

        if (folders.Count > 0 && !DeleteIndexFolders(folders)) return;
        if (files.Count == 0) return;

        if (_loc.Kind == LocKind.IndexFolder && !shift)
        {
            var folderId = _loc.FolderId;
            var recs = files.Select(f => f.Record).OfType<FileRecord>().ToList();
            _store.Batch(() => { foreach (var r in recs) _store.Unlink(folderId, r); });
            RefreshNow();
            Toast($"{recs.Count}개 항목을 ‘{FolderName(folderId)}’ 색인에서 뺐습니다", "원본 파일은 그대로 있습니다.", "실행 취소", () =>
            {
                _store.Batch(() => { foreach (var r in recs) if (_store.File(r.Id) != null) _store.Link(folderId, r); });
                RefreshNow();
            });
            return;
        }

        // 원본이 이미 없는 항목은 색인에서만 지운다
        foreach (var m in files.Where(f => f.Missing).Select(f => f.Record).OfType<FileRecord>()) _store.Remove(m);
        var paths = files.Where(f => !f.Missing).Select(f => f.Path).ToList();
        if (paths.Count == 0) { RefreshNow(); return; }
        if (ShellFileOps.Recycle(this, paths, confirm: true))
        {
            _store.Batch(() =>
            {
                foreach (var p in paths)
                {
                    if (_store.ByPath(p) is { } r && !File.Exists(p)) _store.Remove(r);
                    foreach (var r2 in _store.Files.Where(f => PathUtil.IsUnder(f.Path, p) && !File.Exists(f.Path)).ToList()) _store.Remove(r2);
                }
            });
        }
        RefreshNow();
    }

    private bool DeleteIndexFolders(List<long> ids)
    {
        var all = ids.SelectMany(id => _store.Descendants(id).Append(id)).ToHashSet();
        var fileCount = _store.Files.Count(f => f.Folders.Overlaps(all));
        var name = ids.Count == 1 ? $"‘{FolderName(ids[0])}’ 색인 폴더를" : $"색인 폴더 {ids.Count}개를";
        var msg = $"{name} 삭제할까요?\n\n" + (fileCount > 0
            ? $"연결된 파일 {fileCount:N0}개는 지워지지 않고 원본 위치에 그대로 남습니다.\n(다른 색인에 없는 파일은 미분류 함으로 갑니다.)"
            : "비어 있는 색인 폴더입니다.");
        if (MessageBox.Show(this, msg, "색인 폴더 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return false;
        var goUp = _loc.Kind == LocKind.IndexFolder && (all.Contains(_loc.FolderId));
        var parent = ids.Count == 1 ? _store.Folder(ids[0])?.ParentId : null;
        _store.Batch(() => { foreach (var id in ids) if (_store.Folder(id) != null) _store.DeleteFolder(id); });
        if (goUp) Navigate(parent is long p ? new Location(LocKind.IndexFolder, p) : new Location(LocKind.IndexRoot), record: false);
        else RefreshNow();
        return true;
    }

    private void DeleteTreeNode(TreeNode? node = null)
    {
        node ??= (IndexTree.IsKeyboardFocusWithin ? IndexTree.SelectedItem : PhysTree.SelectedItem) as TreeNode;
        if (node == null) return;
        if (node.Kind == NodeKind.IndexFolder) DeleteIndexFolders([node.FolderId]);
        else if (node.Kind == NodeKind.Dir && node.Parent is { Kind: not NodeKind.ThisPC })
        {
            if (ShellFileOps.Recycle(this, [node.Path], confirm: true))
            {
                node.Parent.LoadChildren();
                if (_loc.Kind == LocKind.Dir && PathUtil.IsUnder(_loc.Path, node.Path))
                    Navigate(new Location(LocKind.Dir, Path: node.Parent.Path), record: false);
                else RefreshNow();
            }
        }
    }

    // ===================== 복사 · 잘라내기 · 붙여넣기 =====================

    private void Cut_Click(object sender, RoutedEventArgs e) => CopySelected(cut: true);
    private void Copy_Click(object sender, RoutedEventArgs e) => CopySelected(cut: false);
    private void Paste_Click(object sender, RoutedEventArgs e) => Paste(_loc);

    private void CopySelected(bool cut)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        ClearCutMarks();
        var paths = sel.Where(i => i.Kind is ItemKind.File or ItemKind.Dir && !i.Missing).Select(i => i.Path).ToList();
        _clip = new ClipState(Guid.NewGuid().ToString("N"), paths,
            sel.Select(i => i.Record?.Id).OfType<long>().ToList(),
            sel.Where(i => i.Kind == ItemKind.IndexFolder).Select(i => i.FolderId).ToList(),
            _loc.Kind == LocKind.IndexFolder ? _loc.FolderId : null, cut);

        var data = new DataObject();
        if (paths.Count > 0)
        {
            var sc = new System.Collections.Specialized.StringCollection();
            sc.AddRange(paths.ToArray());
            data.SetFileDropList(sc);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        }
        data.SetData(ClipFormat, _clip.Token);
        try { Clipboard.SetDataObject(data, true); }
        catch (Exception ex) { Toast("클립보드를 사용할 수 없습니다", ex.Message); return; }
        if (cut) foreach (var i in sel) i.IsCut = true;
    }

    private void ClearCutMarks()
    {
        foreach (var i in _items.Where(i => i.IsCut)) i.IsCut = false;
    }

    private static bool PrefersMove(IDataObject data)
    {
        try
        {
            if (data.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
            {
                var b = new byte[4];
                ms.Position = 0;
                ms.ReadExactly(b);
                return (BitConverter.ToInt32(b) & 2) != 0;
            }
        }
        catch { }
        return false;
    }

    private void Paste(Location target)
    {
        IDataObject? data;
        try { data = Clipboard.GetDataObject(); } catch { return; }
        if (data == null) return;
        var internalClip = _clip != null && data.GetDataPresent(ClipFormat) && data.GetData(ClipFormat) as string == _clip.Token ? _clip : null;
        var files = data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] ?? [] : [];
        var move = internalClip?.Cut ?? PrefersMove(data);
        if (internalClip == null && files.Length == 0) return;

        var done = false;
        switch (target.Kind)
        {
            case LocKind.IndexRoot:
                if (internalClip is { FolderIds.Count: > 0 } && move) { MoveIndexFolders(internalClip.FolderIds, null); done = true; }
                else Toast("색인 맨 위에는 폴더만 둘 수 있습니다", "파일은 색인 폴더 안에 붙여넣으세요.");
                break;

            case LocKind.IndexFolder:
                if (internalClip != null)
                {
                    if (internalClip.FolderIds.Count > 0)
                    {
                        if (move) MoveIndexFolders(internalClip.FolderIds, target.FolderId);
                        else Toast("색인 폴더는 잘라내서 옮길 수 있습니다", "복사 대신 잘라내기(Ctrl+X)를 쓰세요.");
                    }
                    if (move && internalClip.SourceFolder is long src && internalClip.FileIds.Count > 0)
                        MoveLinks(src, target.FolderId, internalClip.FileIds);
                    else if (internalClip.Paths.Count > 0 || internalClip.FileIds.Count > 0)
                    {
                        var n = LinkPaths(target.FolderId, internalClip.Paths);
                        foreach (var id in internalClip.FileIds) if (_store.File(id) is { Missing: true } m) { _store.Link(target.FolderId, m); n++; }
                        Toast($"{n}개 항목을 ‘{FolderName(target.FolderId)}’ 색인에 연결했습니다", "원본 파일은 그대로 있습니다.");
                    }
                    done = true;
                }
                else done = ImportCopy(target.FolderId, files, move);
                break;

            case LocKind.Dir:
                if (files.Length > 0)
                {
                    done = ShellFileOps.TransferInto(this, move, files, target.Path);
                    if (done && internalClip?.Cut == true) ClearCutMarks();
                }
                break;

            default:
                Toast("여기에는 붙여넣을 수 없습니다", "색인 폴더나 원본 폴더를 연 뒤 붙여넣으세요.");
                break;
        }

        if (done && move)
        {
            try { Clipboard.Clear(); } catch { }
            _clip = null;
            ClearCutMarks();
        }
        RefreshNow();
    }

    /// <summary>
    /// 바깥(탐색기 등)에서 복사한 파일을 색인 폴더에 붙여넣기:
    /// 실제 파일을 이 색인의 저장 위치(지정이 없으면 원본 보관함에 같은 구조)로 복사하고 연결한다.
    /// </summary>
    private bool ImportCopy(long folderId, IReadOnlyList<string> sources, bool move)
    {
        var (dir, pinned) = StorageOf(folderId);
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { Toast("저장 위치를 만들 수 없습니다", ex.Message); return false; }

        var targets = new List<string>();
        foreach (var s in sources) targets.Add(ShellFileOps.UniquePath(dir, Path.GetFileName(Path.TrimEndingDirectorySeparator(s)), Directory.Exists(s)));
        var ok = ShellFileOps.Transfer(this, move, sources, targets);
        _indexer.EnsureWatching();

        var n = 0;
        _store.Batch(() =>
        {
            foreach (var t in targets)
            {
                if (File.Exists(t) && _store.UpsertFromDisk(t) is { } rec) { _store.Link(folderId, rec); n++; }
                else if (Directory.Exists(t)) n += LinkDirectory(folderId, t);
            }
        });
        if (n > 0) Toast($"{n}개 파일을 {(move ? "옮겨" : "복사해")} 저장하고 색인에 연결했습니다", StorageNote(dir, pinned));
        return ok;
    }

    /// <summary>파일·폴더 경로를 색인 폴더에 연결 (실제 파일은 그대로). 폴더는 같은 이름의 하위 색인으로.</summary>
    private int LinkPaths(long folderId, IEnumerable<string> paths)
    {
        var n = 0;
        _store.Batch(() =>
        {
            foreach (var p in paths)
            {
                if (File.Exists(p))
                {
                    if ((_store.ByPath(p) ?? _store.UpsertFromDisk(p)) is { } rec) { _store.Link(folderId, rec); n++; }
                }
                else if (Directory.Exists(p)) n += LinkDirectory(folderId, p);
            }
        });
        return n;
    }

    /// <summary>실제 폴더를 같은 구조의 하위 색인 폴더로 만들고 안의 파일을 모두 연결한다.</summary>
    private int LinkDirectory(long parentFolderId, string dir, int depth = 0)
    {
        if (depth > 32) return 0;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        if (string.IsNullOrEmpty(name)) name = dir.TrimEnd('\\', ':');
        var sub = _store.Children(parentFolderId).FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                  ?? _store.CreateFolder(parentFolderId, name);
        var n = 0;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", opts))
                if ((_store.ByPath(f) ?? _store.UpsertFromDisk(f)) is { } rec) { _store.Link(sub.Id, rec); n++; }
            foreach (var d in Directory.EnumerateDirectories(dir, "*", opts))
                n += LinkDirectory(sub.Id, d, depth + 1);
        }
        catch (Exception ex) { App.Log("link dir: " + ex.Message); }
        return n;
    }

    /// <summary>색인 폴더 사이에서 파일 옮기기. 원본 보관함에 있던 파일은 실제 위치도 따라 옮긴다.</summary>
    private void MoveLinks(long src, long dst, IEnumerable<long> fileIds)
    {
        if (src == dst) return;
        var srcDir = StorageOf(src).Dir;
        var (dstDir, _) = StorageOf(dst);
        var n = 0;
        var followFailed = 0;
        _store.Batch(() =>
        {
            foreach (var id in fileIds)
            {
                if (_store.File(id) is not { } rec) continue;
                _store.Unlink(src, rec);
                _store.Link(dst, rec);
                n++;
                // 원본 보관함 안, 이 색인의 저장 폴더에 있고 다른 색인에 없는 파일만 따라 옮긴다
                if (rec.Folders.Count == 1 && !rec.Missing && PathUtil.IsUnder(rec.Path, _settings.StorageRoot)
                    && rec.Dir.Equals(srcDir, StringComparison.OrdinalIgnoreCase) && !srcDir.Equals(dstDir, StringComparison.OrdinalIgnoreCase)
                    && PathUtil.IsUnder(dstDir, _settings.StorageRoot))
                {
                    try
                    {
                        Directory.CreateDirectory(dstDir);
                        var np = ShellFileOps.UniquePath(dstDir, rec.Name);
                        File.Move(rec.Path, np);
                        _store.UpdatePath(rec, np);
                    }
                    catch { followFailed++; }
                }
            }
        });
        Toast($"{n}개 항목을 ‘{FolderName(dst)}’ 색인으로 옮겼습니다",
            followFailed > 0 ? $"{followFailed}개는 사용 중이라 원본 보관함 안에서 옮기지 못했습니다 (색인은 옮겨졌습니다)." : "",
            "열기", () => Navigate(new Location(LocKind.IndexFolder, dst)));
    }

    private void MoveIndexFolders(IEnumerable<long> ids, long? newParent)
    {
        foreach (var id in ids)
        {
            if (_store.Folder(id) is not { } f || f.ParentId == newParent) continue;
            if (newParent is long np && (np == id || _store.IsAncestor(id, np)))
            {
                Toast("폴더를 자기 안으로 옮길 수 없습니다");
                continue;
            }
            FollowStorage(id, () => _store.MoveFolder(id, newParent));
        }
        RefreshNow();
    }

    // ===================== 색인에 추가 =====================

    private void AddToIndexMenu_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected().Where(i => i.Kind is ItemKind.File or ItemKind.Dir).ToList();
        if (sel.Count == 0) { Toast("색인에 추가할 파일을 먼저 선택하세요"); return; }
        var menu = BuildFolderPicker(id => LinkItems(id, sel));
        if (sender is FrameworkElement fe and not MainWindow) OpenMenuUnder(menu, fe);
        else menu.IsOpen = true;
    }

    private ContextMenu BuildFolderPicker(Action<long> pick)
    {
        var menu = new ContextMenu();
        var roots = _store.Children(null).ToList();
        if (roots.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "색인 폴더가 아직 없습니다", IsEnabled = false });
        }
        foreach (var f in roots) menu.Items.Add(FolderMenuItem(f, pick));
        menu.Items.Add(new Separator());
        var create = new MenuItem { Header = "새 색인 폴더를 만들어 추가" };
        create.Click += (_, _) =>
        {
            var parent = _loc.Kind == LocKind.IndexFolder ? _loc.FolderId : (long?)null;
            var nf = _store.CreateFolder(parent);
            pick(nf.Id);
            RefreshNow();
            var node = FindIndexNode(nf.Id);
            if (node != null) { foreach (var a in node.Ancestors()) a.IsExpanded = true; RenameTreeNode(node); }
        };
        menu.Items.Add(create);
        return menu;
    }

    private MenuItem FolderMenuItem(IndexFolder f, Action<long> pick)
    {
        var mi = new MenuItem { Header = f.Name, Icon = MenuIcon(ShellImages.Icon(Settings.DataDir, true)) };
        var kids = _store.Children(f.Id).ToList();
        if (kids.Count == 0)
        {
            mi.Click += (_, _) => pick(f.Id);
            return mi;
        }
        var here = new MenuItem { Header = $"‘{f.Name}’에 추가", FontWeight = FontWeights.SemiBold };
        here.Click += (_, _) => pick(f.Id);
        mi.Items.Add(here);
        mi.Items.Add(new Separator());
        foreach (var k in kids) mi.Items.Add(FolderMenuItem(k, pick));
        return mi;
    }

    private void LinkItems(long folderId, IReadOnlyList<ListItem> items)
    {
        var n = LinkPaths(folderId, items.Where(i => !i.Missing).Select(i => i.Path));
        foreach (var m in items.Where(i => i.Missing).Select(i => i.Record).OfType<FileRecord>()) { _store.Link(folderId, m); n++; }
        RefreshNow();
        Toast($"{n}개 항목을 ‘{FolderName(folderId)}’ 색인에 연결했습니다", "원본 파일은 그대로 있습니다.",
            "열기", () => Navigate(new Location(LocKind.IndexFolder, folderId)));
    }

    // ===================== 저장 위치 =====================

    private void ChooseStoragePath(long folderId)
    {
        var f = _store.Folder(folderId);
        if (f == null) return;
        var current = StorageOf(folderId).Dir;
        var dlg = new OpenFolderDialog
        {
            Title = $"‘{f.Name}’ 색인에서 새로 만들거나 붙여넣은 파일을 저장할 폴더",
            InitialDirectory = Directory.Exists(current) ? current : (Directory.Exists(_settings.StorageRoot) ? _settings.StorageRoot : ""),
        };
        if (dlg.ShowDialog(this) != true) return;
        _store.SetStoragePath(folderId, dlg.FolderName);
        RefreshNow();
        Toast($"‘{f.Name}’ 색인의 저장 위치를 지정했습니다", $"{dlg.FolderName}\n하위 색인 폴더도 이 위치 아래에 같은 구조로 저장합니다.");
    }

    private void ClearStoragePath(long folderId)
    {
        _store.SetStoragePath(folderId, null);
        RefreshNow();
        Toast("기본 저장 위치로 되돌렸습니다", StorageOf(folderId).Dir);
    }

    private void OpenStorageDir(long folderId)
    {
        var dir = StorageOf(folderId).Dir;
        if (Directory.Exists(dir)) Navigate(new Location(LocKind.Dir, Path: dir));
        else Toast("아직 저장된 파일이 없어 폴더가 없습니다", $"{dir}\n이 색인에서 새로 만들거나 붙여넣으면 자동으로 생깁니다.");
    }

    // ===================== 우클릭 메뉴 =====================

    private void List_PreviewRightUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var container = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is ListItem item)
        {
            if (!container.IsSelected)
            {
                List.SelectedItems.Clear();
                container.IsSelected = true;
            }
            container.Focus();
            var sel = Selected();
            if (sel.Count == 0) sel = [item];
            if (sel.All(i => i.Kind is ItemKind.File or ItemKind.Dir or ItemKind.Drive && !i.Missing)) ShowShellMenu(sel);
            else if (sel.All(i => i.Kind == ItemKind.IndexFolder)) IndexFolderMenu(sel.Select(i => i.FolderId).ToList()).IsOpen = true;
            else MixedMenu().IsOpen = true;
        }
        else if (FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(e.OriginalSource as DependencyObject) == null
                 && FindAncestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject) == null)
        {
            List.SelectedItems.Clear();
            BackgroundMenu().IsOpen = true;
        }
    }

    private void ShowShellMenu(List<ListItem> sel)
    {
        var extras = new List<ExtraMenuItem>();
        var files = sel.Where(i => i.Kind is ItemKind.File or ItemKind.Dir).ToList();
        if (_loc.Kind == LocKind.IndexFolder && files.Count > 0)
            extras.Add(new("이 색인에서 빼기 (원본은 그대로)", () => DeleteSelected(shift: false)));
        if (sel.Count == 1 && sel[0].Kind == ItemKind.File && !_loc.IsPhysical)
            extras.Add(new("원본 위치 열기", () => RevealOriginal(sel[0])));
        if (files.Count > 0)
            extras.Add(new("색인에 추가…", () => BuildFolderPicker(id => LinkItems(id, files)).IsOpen = true));
        if (sel.Count == 1 && sel[0].Kind is ItemKind.Dir or ItemKind.Drive)
            extras.Insert(0, new("새 창에서 열기", () => OpenNewWindow(new Location(LocKind.Dir, Path: sel[0].Path))));

        ShellContextMenu.Show(this, sel.Select(i => i.Path).ToList(), extras, verb =>
        {
            switch (verb)
            {
                case "delete": Dispatcher.BeginInvoke(() => DeleteSelected(shift: _loc.Kind == LocKind.IndexFolder)); return true;
                case "rename": Dispatcher.BeginInvoke(BeginRename); return true;
                case "cut": CopySelected(cut: true); return true;
                case "copy": CopySelected(cut: false); return true;
                default: return false;
            }
        });
        Dispatcher.BeginInvoke(() => RefreshNow(), System.Windows.Threading.DispatcherPriority.Background);
    }

    private ContextMenu IndexFolderMenu(List<long> ids)
    {
        var menu = new ContextMenu();
        void Add(string header, Action a, string? gesture = null, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        if (ids.Count == 1)
        {
            var id = ids[0];
            var f = _store.Folder(id);
            Add("열기", () => Navigate(new Location(LocKind.IndexFolder, id)), "Enter");
            Add("새 창에서 열기", () => OpenNewWindow(new Location(LocKind.IndexFolder, id)));
            Add("새 하위 폴더", () => { Navigate(new Location(LocKind.IndexFolder, id)); NewFolder(); });
            Add("이름 바꾸기", () =>
            {
                if (_loc.Kind != LocKind.IndexFolder || _loc.FolderId != id)
                {
                    var item = _items.FirstOrDefault(i => i.Kind == ItemKind.IndexFolder && i.FolderId == id);
                    if (item != null) { SelectKeys(["F" + id]); BeginRename(); return; }
                }
                if (FindIndexNode(id) is { } node) RenameTreeNode(node);
            }, "F2");
            menu.Items.Add(new Separator());
            Add("새 파일 저장 위치 지정…", () => ChooseStoragePath(id));
            if (!string.IsNullOrEmpty(f?.StoragePath)) Add("기본 저장 위치로 되돌리기", () => ClearStoragePath(id));
            Add("저장 폴더 열기", () => OpenStorageDir(id));
            menu.Items.Add(new Separator());
        }
        Add("삭제 (색인 폴더만, 파일은 그대로)", () => DeleteIndexFolders(ids), "Delete");
        return menu;
    }

    private ContextMenu MixedMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "열기" };
        open.Click += (_, _) => OpenSelected();
        menu.Items.Add(open);
        if (_loc.Kind == LocKind.IndexFolder)
        {
            var un = new MenuItem { Header = "이 색인에서 빼기", InputGestureText = "Delete" };
            un.Click += (_, _) => DeleteSelected(shift: false);
            menu.Items.Add(un);
        }
        else
        {
            var del = new MenuItem { Header = "삭제", InputGestureText = "Delete" };
            del.Click += (_, _) => DeleteSelected(shift: false);
            menu.Items.Add(del);
        }
        return menu;
    }

    private ContextMenu BackgroundMenu()
    {
        var menu = new ContextMenu();
        var view = new MenuItem { Header = "보기" };
        var det = new MenuItem { Header = "자세히", IsCheckable = true, IsChecked = !_settings.IconView };
        det.Click += (_, _) => SetViewMode(false);
        var til = new MenuItem { Header = "큰 아이콘", IsCheckable = true, IsChecked = _settings.IconView };
        til.Click += (_, _) => SetViewMode(true);
        view.Items.Add(det);
        view.Items.Add(til);
        menu.Items.Add(view);

        var sort = new MenuItem { Header = "정렬 기준" };
        foreach (var (key, title) in new[] { ("Name", "이름"), ("Modified", "수정한 날짜"), ("Type", "유형"), ("Size", "크기") })
        {
            var mi = new MenuItem { Header = title, IsCheckable = true, IsChecked = _sortKey == key };
            mi.Click += (_, _) => SetSort(key, key == _sortKey ? _sortDesc : key == "Modified");
            sort.Items.Add(mi);
        }
        menu.Items.Add(sort);
        var refresh = new MenuItem { Header = "새로 고침", InputGestureText = "F5" };
        refresh.Click += (_, _) => RefreshAll();
        menu.Items.Add(refresh);

        if (_loc.Kind is LocKind.IndexRoot or LocKind.IndexFolder or LocKind.Dir)
        {
            menu.Items.Add(new Separator());
            var paste = new MenuItem { Header = "붙여넣기", InputGestureText = "Ctrl+V", IsEnabled = CanPaste() };
            paste.Click += (_, _) => Paste(_loc);
            menu.Items.Add(paste);
            var nw = new MenuItem { Header = "새로 만들기" };
            FillNewMenu(nw.Items);
            menu.Items.Add(nw);
        }
        if (_loc.Kind == LocKind.IndexFolder)
        {
            menu.Items.Add(new Separator());
            var id = _loc.FolderId;
            var sp = new MenuItem { Header = "새 파일 저장 위치 지정…" };
            sp.Click += (_, _) => ChooseStoragePath(id);
            menu.Items.Add(sp);
            var op = new MenuItem { Header = "저장 폴더 열기" };
            op.Click += (_, _) => OpenStorageDir(id);
            menu.Items.Add(op);
        }
        if (_loc.Kind == LocKind.Dir)
        {
            menu.Items.Add(new Separator());
            var ex = new MenuItem { Header = "Windows 탐색기에서 열기" };
            var p = _loc.Path;
            ex.Click += (_, _) => { try { Process.Start("explorer.exe", $"\"{p}\""); } catch { } };
            menu.Items.Add(ex);
        }
        return menu;
    }

    private bool CanPaste()
    {
        try { return Clipboard.ContainsFileDropList() || Clipboard.ContainsData(ClipFormat); }
        catch { return false; }
    }

    private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e) => e.Handled = true;

    private void Tree_PreviewRightDown(object sender, MouseButtonEventArgs e)
    {
        var tvi = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (tvi?.DataContext is not TreeNode node || node == TreeNode.Placeholder) return;
        e.Handled = true;
        tvi.Focus();
        switch (node.Kind)
        {
            case NodeKind.IndexFolder:
                IndexFolderMenu([node.FolderId]).IsOpen = true;
                break;
            case NodeKind.IndexRoot:
            {
                var menu = new ContextMenu();
                var nf = new MenuItem { Header = "새 색인 폴더" };
                nf.Click += (_, _) => { Navigate(new Location(LocKind.IndexRoot)); NewFolder(); };
                menu.Items.Add(nf);
                menu.IsOpen = true;
                break;
            }
            case NodeKind.Dir:
                ShellContextMenu.Show(this, [node.Path], [new("새 창에서 열기", () => OpenNewWindow(new Location(LocKind.Dir, Path: node.Path)))], verb =>
                {
                    switch (verb)
                    {
                        case "rename" when node.CanRename: Dispatcher.BeginInvoke(() => RenameTreeNode(node)); return true;
                        case "delete": Dispatcher.BeginInvoke(() => DeleteTreeNode(node)); return true;
                        default: return false;
                    }
                });
                break;
        }
    }

    private void UpdateCommandStates()
    {
        var sel = Selected();
        CutBtn.IsEnabled = CopyBtn.IsEnabled = sel.Any(i => i.Kind is ItemKind.File or ItemKind.Dir or ItemKind.IndexFolder);
        RenameBtn.IsEnabled = sel.Count == 1 && sel[0].Kind != ItemKind.Drive && !sel[0].Missing;
        DeleteBtn.IsEnabled = sel.Count > 0 && sel.All(i => i.Kind != ItemKind.Drive);
        AddIndexBtn.IsEnabled = sel.Any(i => i.Kind is ItemKind.File or ItemKind.Dir);
        PasteBtn.IsEnabled = _loc.Kind is LocKind.IndexRoot or LocKind.IndexFolder or LocKind.Dir;
        NewBtn.IsEnabled = _loc.Kind is LocKind.IndexRoot or LocKind.IndexFolder or LocKind.Dir;
        DeleteBtn.ToolTip = _loc.Kind == LocKind.IndexFolder
            ? "이 색인에서 빼기 (Delete) · 원본은 그대로\n원본을 휴지통으로: Shift+Delete"
            : "휴지통으로 보내기 (Delete)";
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow(_settings, firstRun: false) { Owner = this };
        if (w.ShowDialog() == true)
        {
            _settings.Save();
            _indexer.Start();
            RefreshNow();
        }
    }
}
