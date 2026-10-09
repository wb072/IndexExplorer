using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IndexExplorer.Native;
using IndexExplorer.Services;
using IndexExplorer.ViewModels;

namespace IndexExplorer.Views;

public enum LocKind { IndexRoot, IndexFolder, Inbox, AllFiles, ThisPC, Dir, Search }

/// <summary>지금 보고 있는 곳.</summary>
public sealed record Location(LocKind Kind, long FolderId = 0, string Path = "", string Query = "")
{
    public bool IsPhysical => Kind is LocKind.Dir or LocKind.ThisPC;
    public bool IsIndexFolder => Kind == LocKind.IndexFolder;

    public bool Same(Location o) => Kind == o.Kind && FolderId == o.FolderId && Query == o.Query
                                    && string.Equals(Path, o.Path, StringComparison.OrdinalIgnoreCase);
}

public partial class MainWindow : Window
{
    private readonly Settings _settings;
    private readonly IndexStore _store;
    private readonly Indexer _indexer;

    private readonly ObservableCollection<TreeNode> _indexNodes = [];
    private readonly ObservableCollection<TreeNode> _physNodes = [];
    private TreeNode _indexRoot = null!, _inboxNode = null!, _allNode = null!, _pcNode = null!;

    private GridView _grid = null!;
    private GridViewColumn _colLocation = null!, _colIndex = null!;
    private readonly Dictionary<GridViewColumn, (string key, string title)> _colInfo = [];

    private ObservableCollection<ListItem> _items = [];
    private ListCollectionView _view = null!;
    private string _sortKey = "Name";
    private bool _sortDesc;

    private Location _loc = new(LocKind.IndexRoot);
    private Location _lastIndexLoc = new(LocKind.IndexRoot), _lastPhysLoc = new(LocKind.ThisPC);
    private Location? _beforeSearch;
    private readonly Stack<Location> _back = new(), _fwd = new();

    private readonly DispatcherTimer _refreshTimer, _searchTimer, _toastTimer;
    private FileSystemWatcher? _dirWatcher;
    private bool _syncingTree, _settingMode, _settingSearch;
    private string _folderSignature = "";

    private readonly Location? _startLoc;

    /// <summary>App.OpenWindow 가 위치를 정해 준 창 (저장된 위치로 덮어쓰지 않는다).</summary>
    public bool KeepPosition { get; set; }

    public MainWindow(Location? start = null)
    {
        InitializeComponent();
        _startLoc = start;
        _settings = App.Settings;
        _store = App.Store;
        _indexer = App.Indexer;

        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => OnRefreshTick(), Dispatcher) { IsEnabled = false };
        _searchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(160), DispatcherPriority.Input, (_, _) => { _searchTimer!.Stop(); RunSearch(); }, Dispatcher) { IsEnabled = false };
        _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal, (_, _) => HideToast(), Dispatcher) { IsEnabled = false };

        _store.Changed += OnStoreChanged;
        _indexer.Status += OnIndexerStatus;
        StatusRight.Text = _indexer.LastStatus;
        Closing += OnClosing;
        Loaded += OnLoaded;
    }

    private void OnStoreChanged() { _refreshTimer.Stop(); _refreshTimer.Start(); }
    private void OnIndexerStatus(string s) => StatusRight.Text = s;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ShellImages.Scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (!KeepPosition) RestoreBounds_();
        else SidebarCol.Width = new GridLength(Math.Clamp(_settings.SidebarWidth, 170, 480));

        _grid = (GridView)FindResource("DetailsView");
        string[] keys = ["Name", "Modified", "Type", "Size", "Location", "Index"];
        for (int i = 0; i < _grid.Columns.Count; i++)
            _colInfo[_grid.Columns[i]] = (keys[i], (string)_grid.Columns[i].Header);
        _colLocation = _grid.Columns[4];
        _colIndex = _grid.Columns[5];
        TextSearch.SetTextPath(List, "Name");

        _view = new ListCollectionView(_items) { CustomSort = new ItemComparer(_sortKey, _sortDesc) };
        List.ItemsSource = _view;
        ApplyViewMode();
        ApplyPreviewVisibility();
        UpdateSortHeaders();

        IndexTree.ItemsSource = _indexNodes;
        PhysTree.ItemsSource = _physNodes;
        BuildIndexTree();
        BuildPhysTree();
        Navigate(_startLoc ?? new Location(LocKind.IndexRoot), record: false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.IndexerStarted) { StartDirWatcher(); return; } // 두 번째 이후 창
        App.IndexerStarted = true;
        if (App.SelfTestOut != null)
        {
            _indexer.Start();
            _ = RunSelfTest();
            return;
        }
        if (!_settings.FirstRunDone)
        {
            // 처음 실행: 어떤 폴더를 색인할지 고른다
            var w = new SettingsWindow(_settings, firstRun: true) { Owner = this };
            w.ShowDialog();
            _settings.FirstRunDone = true;
            _settings.Save();
        }
        _indexer.Start();
        StartDirWatcher();
    }

    private void RestoreBounds_()
    {
        if (!double.IsNaN(_settings.Left) && !double.IsNaN(_settings.Top))
        {
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (vs.Contains(new Point(_settings.Left + 40, _settings.Top + 20)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _settings.Left; Top = _settings.Top;
            }
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = Math.Max(MinWidth, _settings.Width);
        Height = Math.Max(MinHeight, _settings.Height);
        if (_settings.Maximized) WindowState = WindowState.Maximized;
        SidebarCol.Width = new GridLength(Math.Clamp(_settings.SidebarWidth, 170, 480));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.Left = r.Left; _settings.Top = r.Top; _settings.Width = r.Width; _settings.Height = r.Height;
        _settings.Maximized = WindowState == WindowState.Maximized;
        _settings.SidebarWidth = SidebarCol.ActualWidth;
        _settings.Save();
        _dirWatcher?.Dispose();
        _store.Changed -= OnStoreChanged;
        _indexer.Status -= OnIndexerStatus;
        _refreshTimer.Stop();
        _toastTimer.Stop();
    }

    // ===================== 트리 =====================

    private static IEnumerable<TreeNode> AllNodes(IEnumerable<TreeNode> roots)
    {
        foreach (var n in roots)
        {
            if (n == TreeNode.Placeholder) continue;
            yield return n;
            foreach (var c in AllNodes(n.Children)) yield return c;
        }
    }

    private string FolderSignature() =>
        string.Join("|", _store.Folders.OrderBy(f => f.Id).Select(f => $"{f.Id}:{f.ParentId}:{f.Name}:{f.StoragePath}"));

    private void BuildIndexTree()
    {
        var expanded = _indexNodes.Count == 0
            ? []
            : AllNodes(_indexNodes).Where(n => n.IsExpanded && n.Kind == NodeKind.IndexFolder).Select(n => n.FolderId).ToHashSet();
        var rootExpanded = _indexRoot?.IsExpanded ?? true;

        _indexRoot = new TreeNode { Kind = NodeKind.IndexRoot, Name = "색인", Icon = (ImageSource)FindResource("IconIndexRoot"), IsExpanded = rootExpanded };
        AddFolderNodes(_indexRoot, null, expanded);
        _inboxNode = new TreeNode { Kind = NodeKind.Inbox, Name = "미분류 함", Icon = (ImageSource)FindResource("IconInbox") };
        _allNode = new TreeNode { Kind = NodeKind.AllFiles, Name = "모든 파일", Icon = (ImageSource)FindResource("IconAllFiles") };

        _syncingTree = true;
        try
        {
            _indexNodes.Clear();
            _indexNodes.Add(_indexRoot);
            _indexNodes.Add(_inboxNode);
            _indexNodes.Add(_allNode);
        }
        finally { _syncingTree = false; }
        _folderSignature = FolderSignature();
        UpdateBadges();
    }

    private void AddFolderNodes(TreeNode parent, long? parentId, HashSet<long> expanded)
    {
        foreach (var f in _store.Children(parentId))
        {
            var n = new TreeNode
            {
                Kind = NodeKind.IndexFolder, FolderId = f.Id, Name = f.Name, Parent = parent,
                Icon = ShellImages.Icon(Settings.DataDir, true), IsPinned = !string.IsNullOrEmpty(f.StoragePath),
                IsExpanded = expanded.Contains(f.Id),
            };
            parent.Children.Add(n);
            AddFolderNodes(n, f.Id, expanded);
        }
    }

    private void BuildPhysTree()
    {
        _physNodes.Clear();
        (string path, string name)[] quick =
        [
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "바탕 화면"),
            (KnownFolders.Downloads, "다운로드"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "문서"),
        ];
        foreach (var (p, name) in quick)
        {
            if (!Directory.Exists(p)) continue;
            var n = TreeNode.CreateDir(p, null, name);
            n.Icon = ShellImages.Icon(p, true, perPath: true);
            _physNodes.Add(n);
        }
        _pcNode = new TreeNode
        {
            Kind = NodeKind.ThisPC, Name = "내 PC", IsExpanded = true,
            Icon = ShellImages.Icon("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", true, perPath: true),
        };
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.Unknown or DriveType.NoRootDirectory) continue;
            var n = TreeNode.CreateDir(d.RootDirectory.FullName, _pcNode, ShellImages.DisplayName(d.RootDirectory.FullName));
            n.Icon = ShellImages.Icon(d.RootDirectory.FullName, true, perPath: true);
            _pcNode.Children.Add(n);
        }
        _physNodes.Add(_pcNode);
    }

    private void UpdateBadges()
    {
        var n = _store.UnclassifiedCount;
        _inboxNode.Badge = n > 0 ? n.ToString("N0") : "";
    }

    private TreeNode? FindIndexNode(long folderId) =>
        AllNodes(_indexNodes).FirstOrDefault(n => n.Kind == NodeKind.IndexFolder && n.FolderId == folderId);

    private TreeNode? RevealPhysNode(string path)
    {
        TreeNode? start = _physNodes.Where(n => n.Kind == NodeKind.Dir && PathUtil.IsUnder(path, n.Path))
                              .OrderByDescending(n => n.Path.Length).FirstOrDefault()
                          ?? _pcNode.Children.FirstOrDefault(n => PathUtil.IsUnder(path, n.Path));
        if (start == null) return null;
        var cur = start;
        while (!Path.TrimEndingDirectorySeparator(cur.Path).Equals(Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))
        {
            if (!cur.ChildrenLoaded) cur.LoadChildren();
            var next = cur.Children.FirstOrDefault(c => c != TreeNode.Placeholder && PathUtil.IsUnder(path, c.Path));
            if (next == null) break;
            cur.IsExpanded = true;
            cur = next;
        }
        return cur;
    }

    /// <summary>지금 위치를 트리에서 선택하고, 그 상위로 이어지는 가이드 선을 강조한다.</summary>
    private void SyncTreeSelection()
    {
        _syncingTree = true;
        try
        {
            foreach (var n in AllNodes(_indexNodes).Concat(AllNodes(_physNodes)))
            {
                n.IsOnPath = false;
                n.IsSelected = false;
            }
            TreeNode? target = _loc.Kind switch
            {
                LocKind.IndexRoot => _indexRoot,
                LocKind.IndexFolder => FindIndexNode(_loc.FolderId),
                LocKind.Inbox => _inboxNode,
                LocKind.AllFiles => _allNode,
                LocKind.ThisPC => _pcNode,
                LocKind.Dir => RevealPhysNode(_loc.Path),
                _ => null,
            };
            if (target == null) return;
            foreach (var a in target.Ancestors()) a.IsExpanded = true;
            target.IsSelected = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => BringNodeIntoView(target));
        }
        finally { _syncingTree = false; }
    }

    private void BringNodeIntoView(TreeNode node)
    {
        var tree = node.IsIndex || node.Kind is NodeKind.Inbox or NodeKind.AllFiles ? IndexTree : PhysTree;
        var chain = node.Ancestors().Reverse().Append(node).ToList();
        ItemsControl parent = tree;
        foreach (var n in chain)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(n) is not TreeViewItem tvi) return;
            parent = tvi;
        }
        (parent as TreeViewItem)?.BringIntoView();
    }

    // 마우스를 올린 항목과 같은 단계의 가이드 선만 보인다
    private TreeNode? _guideNode;

    private void Tree_MouseMove(object sender, MouseEventArgs e)
    {
        var node = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as TreeNode;
        var guide = node?.Parent;
        if (guide == _guideNode) return;
        if (_guideNode != null) _guideNode.IsOnPath = false;
        _guideNode = guide;
        if (guide != null) guide.IsOnPath = true;
    }

    private void Tree_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_guideNode != null) _guideNode.IsOnPath = false;
        _guideNode = null;
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingTree || e.NewValue is not TreeNode node || node == TreeNode.Placeholder || !node.IsSelected) return;
        // 트리 항목이 처음 그려질 때도 이 이벤트가 오므로, 이미 보고 있는 곳이면 무시
        var target = LocationOf(node);
        if (_loc.Same(target)) return;
        Navigate(target);
    }

    private static Location LocationOf(TreeNode n) => n.Kind switch
    {
        NodeKind.IndexFolder => new Location(LocKind.IndexFolder, n.FolderId),
        NodeKind.Inbox => new Location(LocKind.Inbox),
        NodeKind.AllFiles => new Location(LocKind.AllFiles),
        NodeKind.ThisPC => new Location(LocKind.ThisPC),
        NodeKind.Dir => new Location(LocKind.Dir, Path: n.Path),
        _ => new Location(LocKind.IndexRoot),
    };

    // ===================== 색인 ↔ 원본 전환 =====================

    private void SetMode(bool physical)
    {
        _settingMode = true;
        try
        {
            ModeIndex.IsChecked = !physical;
            ModePhys.IsChecked = physical;
            IndexTree.Visibility = physical ? Visibility.Collapsed : Visibility.Visible;
            PhysTree.Visibility = physical ? Visibility.Visible : Visibility.Collapsed;
            ModeCaption.Text = physical ? "디스크의 실제 파일 위치" : "찾기 위한 목차 · 파일은 제자리에";
        }
        finally { _settingMode = false; }
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_settingMode || !IsLoaded) return;
        Navigate(sender == ModePhys ? _lastPhysLoc : _lastIndexLoc);
    }

    // ===================== 이동 =====================

    private void Navigate(Location loc, bool record = true, string? selectPath = null)
    {
        CancelAllEdits();
        if (record && !_loc.Same(loc))
        {
            _back.Push(_loc);
            _fwd.Clear();
        }
        if (loc.Kind != LocKind.Search && SearchBox.Text.Length > 0)
        {
            _beforeSearch = null;
            SetSearchText("");
        }
        _loc = loc;
        if (loc.IsPhysical) { _lastPhysLoc = loc; SetMode(true); }
        else if (loc.Kind != LocKind.Search) { _lastIndexLoc = loc; SetMode(false); }

        LoadItems(reset: true);
        if (selectPath != null) SelectPaths([selectPath]);
        UpdateCrumb();
        SyncTreeSelection();
        UpdateNavButtons();
        StartDirWatcher();
    }

    private void UpdateNavButtons()
    {
        BackBtn.IsEnabled = _back.Count > 0;
        FwdBtn.IsEnabled = _fwd.Count > 0;
        UpBtn.IsEnabled = ParentOf(_loc) != null;
    }

    private Location? ParentOf(Location loc) => loc.Kind switch
    {
        LocKind.IndexFolder => _store.Folder(loc.FolderId)?.ParentId is long p
            ? new Location(LocKind.IndexFolder, p) : new Location(LocKind.IndexRoot),
        LocKind.Dir => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(loc.Path)) is { } parent && parent.Length > 0
            ? new Location(LocKind.Dir, Path: parent) : new Location(LocKind.ThisPC),
        LocKind.Search => _beforeSearch,
        _ => null,
    };

    private void GoBack()
    {
        if (_back.Count == 0) return;
        _fwd.Push(_loc);
        Navigate(_back.Pop(), record: false);
    }

    private void GoForward()
    {
        if (_fwd.Count == 0) return;
        _back.Push(_loc);
        Navigate(_fwd.Pop(), record: false);
    }

    private void GoUp()
    {
        var from = _loc;
        if (ParentOf(_loc) is not { } p) return;
        // 올라간 뒤 방금 있던 폴더를 선택해 둔다 (탐색기와 같음)
        Navigate(p, selectPath: from.Kind == LocKind.Dir ? from.Path : null);
        if (from.Kind == LocKind.IndexFolder) SelectKeys(["F" + from.FolderId]);
    }

    private void NewWindow_Click(object sender, RoutedEventArgs e) => OpenNewWindow();

    /// <summary>지금 보고 있는 곳을 새 창으로 (검색 중이면 검색 전 위치).</summary>
    private void OpenNewWindow(Location? at = null) =>
        App.OpenWindow(at ?? (_loc.Kind == LocKind.Search ? _beforeSearch ?? _lastIndexLoc : _loc), this);

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => GoForward();
    private void Up_Click(object sender, RoutedEventArgs e) => GoUp();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshAll();

    private void RefreshAll()
    {
        if (_loc.IsPhysical)
        {
            foreach (var n in AllNodes(_physNodes).Where(n => n.Kind == NodeKind.Dir && n.ChildrenLoaded && !n.IsExpanded).ToList())
            {
                n.ChildrenLoaded = false;
                n.Children.Clear();
                n.Children.Add(TreeNode.Placeholder);
            }
        }
        LoadItems(reset: false);
        _ = _indexer.FullScanAsync();
    }

    /// <summary>지금 보고 있는 원본 폴더가 바뀌면 목록을 새로 고친다 (색인 감시 범위 밖이어도).</summary>
    private void StartDirWatcher()
    {
        _dirWatcher?.Dispose();
        _dirWatcher = null;
        if (_loc.Kind != LocKind.Dir || !Directory.Exists(_loc.Path)) return;
        try
        {
            _dirWatcher = new FileSystemWatcher(_loc.Path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler h = (_, _) => Dispatcher.BeginInvoke(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
            _dirWatcher.Created += h; _dirWatcher.Deleted += h; _dirWatcher.Changed += h;
            _dirWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
            _dirWatcher.EnableRaisingEvents = true;
        }
        catch { }
    }

    private void OnRefreshTick()
    {
        _refreshTimer.Stop();
        if (_items.Any(i => i.IsEditing) || AllNodes(_indexNodes).Any(n => n.IsEditing)) { _refreshTimer.Start(); return; }

        if (FolderSignature() != _folderSignature)
        {
            BuildIndexTree();
            SyncTreeSelection();
        }
        else UpdateBadges();

        // 보고 있던 곳이 사라졌으면 위로
        if (_loc.Kind == LocKind.IndexFolder && _store.Folder(_loc.FolderId) == null) { Navigate(new Location(LocKind.IndexRoot), record: false); return; }
        if (_loc.Kind == LocKind.Dir && !Directory.Exists(_loc.Path))
        {
            var p = _loc.Path;
            while (!Directory.Exists(p) && Path.GetDirectoryName(p) is { } up) p = up;
            Navigate(Directory.Exists(p) ? new Location(LocKind.Dir, Path: p) : new Location(LocKind.ThisPC), record: false);
            return;
        }
        LoadItems(reset: false);
        UpdateCrumb();
    }

    // ===================== 목록 =====================

    private ListItem FileItem(FileRecord r) => new()
    {
        Kind = ItemKind.File, Path = r.Path, Name = r.Name, Record = r, Modified = r.Modified, Size = r.Size,
        Missing = r.Missing, TypeName = ShellImages.TypeName(r.Path, false), Location = r.Dir, IndexText = IndexNames(r),
    };

    private ListItem FolderItem(IndexFolder f, bool withLocation = false) => new()
    {
        Kind = ItemKind.IndexFolder, FolderId = f.Id, Name = f.Name, TypeName = "색인 폴더",
        Location = withLocation && f.ParentId is long p ? "색인 › " + _store.ChainText(p) : withLocation ? "색인" : "",
    };

    private string IndexNames(FileRecord r) =>
        string.Join(", ", r.Folders.Select(id => _store.Folder(id)?.Name).OfType<string>().Order(NaturalComparer.Instance));

    private List<ListItem> BuildItems(out string? error)
    {
        error = null;
        var list = new List<ListItem>();
        switch (_loc.Kind)
        {
            case LocKind.IndexRoot:
                list.AddRange(_store.Children(null).Select(f => FolderItem(f)));
                break;
            case LocKind.IndexFolder:
                list.AddRange(_store.Children(_loc.FolderId).Select(f => FolderItem(f)));
                list.AddRange(_store.FilesIn(_loc.FolderId).Select(FileItem));
                break;
            case LocKind.Inbox:
                list.AddRange(_store.Unclassified().Select(FileItem));
                break;
            case LocKind.AllFiles:
                list.AddRange(_store.Files.Select(FileItem));
                break;
            case LocKind.Search:
                list.AddRange(_store.SearchFolders(_loc.Query).Select(f => FolderItem(f, withLocation: true)));
                list.AddRange(_store.SearchFiles(_loc.Query).Take(5000).Select(FileItem));
                break;
            case LocKind.ThisPC:
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (!d.IsReady || d.DriveType is DriveType.Unknown or DriveType.NoRootDirectory) continue;
                    var root = d.RootDirectory.FullName;
                    list.Add(new ListItem
                    {
                        Kind = ItemKind.Drive, Path = root, Name = ShellImages.DisplayName(root),
                        TypeName = d.DriveType switch
                        {
                            DriveType.Fixed => "로컬 디스크", DriveType.Removable => "이동식 디스크",
                            DriveType.Network => "네트워크 드라이브", DriveType.CDRom => "CD 드라이브", _ => "드라이브",
                        },
                        Location = $"{ListItem.FormatBytes(d.AvailableFreeSpace)} 남음 / 전체 {ListItem.FormatBytes(d.TotalSize)}",
                    });
                }
                break;
            case LocKind.Dir:
                try
                {
                    var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
                    foreach (var e in new DirectoryInfo(_loc.Path).EnumerateFileSystemInfos("*", opts))
                    {
                        if (e is DirectoryInfo d)
                            list.Add(new ListItem { Kind = ItemKind.Dir, Path = d.FullName, Name = d.Name, Modified = d.LastWriteTimeUtc, TypeName = "파일 폴더" });
                        else if (e is FileInfo f)
                        {
                            var rec = _store.ByPath(f.FullName);
                            list.Add(new ListItem
                            {
                                Kind = ItemKind.File, Path = f.FullName, Name = f.Name, Record = rec, Modified = f.LastWriteTimeUtc, Size = f.Length,
                                TypeName = ShellImages.TypeName(f.FullName, false), Location = f.DirectoryName ?? "",
                                IndexText = rec != null ? IndexNames(rec) : "",
                            });
                        }
                    }
                }
                catch (Exception ex) { error = ex is UnauthorizedAccessException ? "이 폴더에 접근할 권한이 없습니다." : ex.Message; }
                break;
        }
        return list;
    }

    /// <param name="reset">다른 곳으로 이동 = 처음부터 다시. 아니면 바뀐 항목만 갱신해 선택·스크롤을 유지한다.</param>
    private void LoadItems(bool reset)
    {
        var list = BuildItems(out var error);
        if (reset)
        {
            // 다른 곳으로 이동: 컬렉션을 통째로 바꿔 수천 개도 한 번에 그린다
            _items = new ObservableCollection<ListItem>(list);
            _view = new ListCollectionView(_items) { CustomSort = new ItemComparer(_sortKey, _sortDesc) };
            List.ItemsSource = _view;
            if (List.Items.Count > 0) List.ScrollIntoView(List.Items[0]);
        }
        else SyncItems(list);

        UpdateColumns();
        UpdateEmptyState(error);
        UpdateStatus();
        UpdatePreview();
        UpdateCommandStates();
    }

    private void SyncItems(List<ListItem> fresh)
    {
        var freshByKey = new Dictionary<string, ListItem>();
        foreach (var i in fresh) freshByKey.TryAdd(i.Key, i);
        var oldByKey = new Dictionary<string, ListItem>();
        foreach (var i in _items) oldByKey.TryAdd(i.Key, i);

        for (int i = _items.Count - 1; i >= 0; i--)
            if (!freshByKey.ContainsKey(_items[i].Key)) _items.RemoveAt(i);

        foreach (var n in freshByKey.Values)
        {
            if (!oldByKey.TryGetValue(n.Key, out var o)) { _items.Add(n); continue; }
            if (SameItem(o, n)) continue;
            var wasSelected = List.SelectedItems.Contains(o);
            var idx = _items.IndexOf(o);
            if (idx < 0) continue;
            n.IsCut = o.IsCut;
            _items[idx] = n;
            if (wasSelected) List.SelectedItems.Add(n);
        }
    }

    private static bool SameItem(ListItem a, ListItem b) =>
        a.Name == b.Name && a.Path == b.Path && a.Modified == b.Modified && a.Size == b.Size && a.Missing == b.Missing
        && a.IndexText == b.IndexText && a.Location == b.Location && a.Record == b.Record;

    private void UpdateColumns()
    {
        var showLoc = _loc.Kind is LocKind.IndexFolder or LocKind.Inbox or LocKind.AllFiles or LocKind.Search or LocKind.ThisPC;
        var showIdx = _loc.Kind is LocKind.Dir or LocKind.AllFiles or LocKind.Search;
        _colInfo[_colLocation] = ("Location", _loc.Kind == LocKind.ThisPC ? "여유 공간" : "원본 위치");
        // 이 앱의 핵심 정보(원본 위치·색인)는 이름 바로 옆에
        SetColumn(_colIndex, false, 0);
        SetColumn(_colLocation, false, 0);
        if (showIdx) SetColumn(_colIndex, true, 1);
        if (showLoc) SetColumn(_colLocation, true, 1);
        UpdateSortHeaders();
    }

    private void SetColumn(GridViewColumn col, bool show, int pos)
    {
        var has = _grid.Columns.Contains(col);
        if (show && !has) _grid.Columns.Insert(Math.Min(pos, _grid.Columns.Count), col);
        else if (!show && has) _grid.Columns.Remove(col);
    }

    private void UpdateEmptyState(string? error)
    {
        if (_items.Count > 0) { EmptyState.Visibility = Visibility.Collapsed; return; }
        (string title, string body) = error != null ? ("폴더를 열 수 없습니다", error) : _loc.Kind switch
        {
            LocKind.IndexRoot => ("첫 색인 폴더를 만들어 보세요", "새로 만들기 › 폴더 (Ctrl+Shift+N)\n업무·고객·프로젝트처럼 찾을 때 떠올리는 이름으로 만드세요."),
            LocKind.IndexFolder => ("이 색인은 비어 있습니다", "원본이나 미분류 함에서 파일을 끌어다 놓으면 연결됩니다.\n파일은 원래 자리에 그대로 있습니다."),
            LocKind.Inbox => ("모두 분류했습니다", "새로 생기는 파일은 여기에 먼저 모입니다."),
            LocKind.Search => ("검색 결과가 없습니다", "다른 단어로 찾아보세요. 띄어 쓴 단어는 모두 포함된 파일을 찾습니다."),
            LocKind.AllFiles => ("색인된 파일이 없습니다", "설정에서 색인할 폴더를 추가하세요."),
            _ => ("이 폴더는 비어 있습니다", ""),
        };
        EmptyTitle.Text = title;
        EmptyBody.Text = body;
        EmptyBody.Visibility = body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void UpdateStatus()
    {
        var sel = List.SelectedItems.Cast<ListItem>().ToList();
        var text = $"항목 {_items.Count:N0}개";
        if (sel.Count > 0)
        {
            text += $"     {sel.Count:N0}개 선택함";
            var size = sel.Where(i => i.Size.HasValue).Sum(i => i.Size!.Value);
            if (size > 0) text += $"  ·  {ListItem.FormatBytes(size)}";
        }
        StatusLeft.Text = text;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_marqueeUpdating) return; // 범위 선택 중에는 끝날 때 한 번만 갱신
        if (List.SelectedItems.Count == 1) _anchor = List.SelectedItem as ListItem;
        UpdateStatus();
        UpdatePreview();
        UpdateCommandStates();
    }

    private List<ListItem> Selected() => List.SelectedItems.Cast<ListItem>().ToList();

    private void SelectKeys(IEnumerable<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List.SelectedItems.Clear();
        ListItem? first = null;
        foreach (var i in _items.Where(i => set.Contains(i.Key)))
        {
            List.SelectedItems.Add(i);
            first ??= i;
        }
        if (first != null)
        {
            List.ScrollIntoView(first);
            FocusItem(first);
        }
    }

    private void SelectPaths(IEnumerable<string> paths) => SelectKeys(paths.Select(p => "P" + p.ToLowerInvariant()));

    private void FocusItem(ListItem item)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (List.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem c) c.Focus();
        });
    }

    // ===================== 정렬 =====================

    private sealed class ItemComparer(string key, bool desc) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var a = (ListItem)x!;
            var b = (ListItem)y!;
            int fa = a.IsFolderLike ? 0 : 1, fb = b.IsFolderLike ? 0 : 1;
            if (fa != fb) return fa - fb; // 폴더가 항상 위
            int r = key switch
            {
                "Modified" => Nullable.Compare(a.Modified, b.Modified),
                "Type" => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCulture),
                "Size" => Nullable.Compare(a.Size, b.Size),
                "Location" => NaturalComparer.Instance.Compare(a.Location, b.Location),
                "Index" => NaturalComparer.Instance.Compare(a.IndexText, b.IndexText),
                _ => NaturalComparer.Instance.Compare(a.Name, b.Name),
            };
            if (r != 0) return desc ? -r : r;
            return NaturalComparer.Instance.Compare(a.Name, b.Name);
        }
    }

    private void SetSort(string key, bool? desc = null)
    {
        _sortDesc = desc ?? (key == _sortKey ? !_sortDesc : key == "Modified");
        _sortKey = key;
        _view.CustomSort = new ItemComparer(_sortKey, _sortDesc);
        UpdateSortHeaders();
    }

    private void UpdateSortHeaders()
    {
        foreach (var (col, (key, title)) in _colInfo)
            col.Header = key == _sortKey ? $"{title}  {(_sortDesc ? "˅" : "˄")}" : title;
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Column: { } col } && _colInfo.TryGetValue(col, out var info))
            SetSort(info.key);
    }

    private void SortMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var (key, title) in new[] { ("Name", "이름"), ("Modified", "수정한 날짜"), ("Type", "유형"), ("Size", "크기"), ("Location", "원본 위치") })
        {
            var mi = new MenuItem { Header = title, IsCheckable = true, IsChecked = _sortKey == key };
            mi.Click += (_, _) => SetSort(key, key == _sortKey ? _sortDesc : key == "Modified");
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var asc = new MenuItem { Header = "오름차순", IsCheckable = true, IsChecked = !_sortDesc };
        asc.Click += (_, _) => SetSort(_sortKey, false);
        var desc = new MenuItem { Header = "내림차순", IsCheckable = true, IsChecked = _sortDesc };
        desc.Click += (_, _) => SetSort(_sortKey, true);
        menu.Items.Add(asc);
        menu.Items.Add(desc);
        OpenMenuUnder(menu, (FrameworkElement)sender);
    }

    private static void OpenMenuUnder(ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ===================== 보기 =====================

    private void ApplyViewMode()
    {
        var tiles = _settings.IconView;
        if (tiles)
        {
            List.View = null;
            List.ItemContainerStyle = (Style)FindResource("TileItem");
            List.ItemTemplate = (DataTemplate)FindResource("TileTemplate");
            List.ItemsPanel = (ItemsPanelTemplate)FindResource("TilesPanel");
            ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Disabled);
        }
        else
        {
            List.ItemTemplate = null;
            List.ItemsPanel = (ItemsPanelTemplate)FindResource("RowsPanel");
            List.ItemContainerStyle = (Style)FindResource("RowItem");
            List.View = _grid;
            ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Auto);
        }
        DetailsBtn.IsChecked = !tiles;
        TilesBtn.IsChecked = tiles;
    }

    private void DetailsView_Click(object sender, RoutedEventArgs e) => SetViewMode(false);
    private void TilesView_Click(object sender, RoutedEventArgs e) => SetViewMode(true);

    private void SetViewMode(bool tiles)
    {
        _settings.IconView = tiles;
        ApplyViewMode();
    }

    private void Tile_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ListItem item }) item.EnsureThumbnail();
    }

    private void PreviewToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.PreviewPane = !_settings.PreviewPane;
        ApplyPreviewVisibility();
    }

    private void ApplyPreviewVisibility()
    {
        var show = _settings.PreviewPane;
        PreviewCol.Width = show ? new GridLength(290) : new GridLength(0);
        PreviewPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PreviewSplitter.Visibility = PreviewPane.Visibility;
        PreviewBtn.IsChecked = show;
        if (show) UpdatePreview();
    }

    // ===================== 주소 표시줄 =====================

    private void UpdateCrumb()
    {
        var segs = new List<CrumbSegment>();
        void Add(string text, Location target) => segs.Add(new CrumbSegment { Text = text, IsFirst = segs.Count == 0, Target = target });
        switch (_loc.Kind)
        {
            case LocKind.IndexRoot:
                Add("색인", _loc);
                break;
            case LocKind.IndexFolder:
                Add("색인", new Location(LocKind.IndexRoot));
                foreach (var f in _store.Chain(_loc.FolderId)) Add(f.Name, new Location(LocKind.IndexFolder, f.Id));
                break;
            case LocKind.Inbox: Add("미분류 함", _loc); break;
            case LocKind.AllFiles: Add("모든 파일", _loc); break;
            case LocKind.Search: Add($"‘{_loc.Query}’ 검색 결과", _loc); break;
            case LocKind.ThisPC: Add("내 PC", _loc); break;
            case LocKind.Dir:
                Add("내 PC", new Location(LocKind.ThisPC));
                var root = Path.GetPathRoot(_loc.Path) ?? _loc.Path;
                Add(ShellImages.DisplayName(root), new Location(LocKind.Dir, Path: root));
                var acc = root;
                foreach (var part in _loc.Path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    acc = Path.Combine(acc, part);
                    Add(part, new Location(LocKind.Dir, Path: acc));
                }
                break;
        }
        CrumbItems.ItemsSource = segs;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => CrumbScroll.ScrollToRightEnd());
        Title = segs.Count > 0 ? $"{segs[^1].Text} - 색인 탐색기" : "색인 탐색기";
    }

    private void Crumb_SegmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CrumbSegment { Target: Location l } }) Navigate(l);
    }

    private void CrumbBox_MouseDown(object sender, MouseButtonEventArgs e) => BeginAddressEdit();

    private void BeginAddressEdit()
    {
        AddressBox.Text = _loc.Kind switch
        {
            LocKind.Dir => _loc.Path,
            LocKind.IndexRoot => "색인",
            LocKind.IndexFolder => "색인\\" + string.Join("\\", _store.Chain(_loc.FolderId).Select(f => f.Name)),
            LocKind.ThisPC => "내 PC",
            _ => "",
        };
        CrumbItems.Visibility = Visibility.Hidden;
        AddressBox.Visibility = Visibility.Visible;
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void EndAddressEdit()
    {
        AddressBox.Visibility = Visibility.Collapsed;
        CrumbItems.Visibility = Visibility.Visible;
    }

    private void AddressBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndAddressEdit();

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { EndAddressEdit(); List.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter)
        {
            var text = AddressBox.Text.Trim().Trim('"');
            EndAddressEdit();
            NavigateAddress(text);
            e.Handled = true;
        }
    }

    private void NavigateAddress(string text)
    {
        if (text.Length == 0) return;
        if (text == "내 PC") { Navigate(new Location(LocKind.ThisPC)); return; }
        if (text.StartsWith("색인", StringComparison.Ordinal))
        {
            long? cur = null;
            foreach (var name in text.Split(['\\', '/', '›'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1))
            {
                var next = _store.Children(cur).FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (next == null) { Toast($"‘{name}’ 색인 폴더를 찾을 수 없습니다."); return; }
                cur = next.Id;
            }
            Navigate(cur is long id ? new Location(LocKind.IndexFolder, id) : new Location(LocKind.IndexRoot));
            return;
        }
        var p = Environment.ExpandEnvironmentVariables(text);
        try
        {
            if (Directory.Exists(p)) { Navigate(new Location(LocKind.Dir, Path: Path.GetFullPath(p))); return; }
            if (File.Exists(p)) { Navigate(new Location(LocKind.Dir, Path: PathUtil.Dir(Path.GetFullPath(p))), selectPath: Path.GetFullPath(p)); return; }
        }
        catch { }
        Toast($"‘{text}’을(를) 찾을 수 없습니다.", "철자를 확인하고 다시 시도하세요.");
    }

    // ===================== 검색 =====================

    private void SetSearchText(string text)
    {
        _settingSearch = true;
        SearchBox.Text = text;
        _settingSearch = false;
        UpdateSearchChrome();
    }

    private void UpdateSearchChrome()
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchClear.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchChrome();
        if (_settingSearch) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void RunSearch()
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0)
        {
            if (_loc.Kind == LocKind.Search) Navigate(_beforeSearch ?? _lastIndexLoc, record: false);
            return;
        }
        if (_loc.Kind != LocKind.Search) _beforeSearch = _loc;
        var before = _beforeSearch;
        var loc = new Location(LocKind.Search, Query: q);
        if (_loc.Kind == LocKind.Search) { _loc = loc; LoadItems(reset: true); UpdateCrumb(); return; }
        Navigate(loc);
        _beforeSearch = before;
    }

    private void ClearSearch()
    {
        if (SearchBox.Text.Length == 0) return;
        SearchBox.Text = "";
        _searchTimer.Stop();
        RunSearch();
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e) { ClearSearch(); SearchBox.Focus(); }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { ClearSearch(); List.Focus(); e.Handled = true; }
        else if (e.Key is Key.Down or Key.Enter)
        {
            _searchTimer.Stop();
            RunSearch();
            if (List.Items.Count > 0)
            {
                List.SelectedIndex = 0;
                FocusItem((ListItem)List.Items[0]);
            }
            e.Handled = true;
        }
    }

    // ===================== 알림 =====================

    private Action? _toastAction;

    private void Toast(string title, string body = "", string? actionText = null, Action? action = null)
    {
        ToastTitle.Text = title;
        ToastBody.Text = body;
        ToastBody.Visibility = body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _toastAction = action;
        ToastAction.Content = actionText;
        ToastAction.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
        ToastBox.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(action != null ? 7 : 4.5);
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        ToastBox.Visibility = Visibility.Collapsed;
    }

    private void ToastAction_Click(object sender, RoutedEventArgs e)
    {
        var a = _toastAction;
        HideToast();
        a?.Invoke();
    }

    // ===================== 키보드·마우스 =====================

    private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) { GoBack(); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { GoForward(); e.Handled = true; }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods == ModifierKeys.Control, alt = mods == ModifierKeys.Alt, none = mods == ModifierKeys.None;
        bool ctrlShift = mods == (ModifierKeys.Control | ModifierKeys.Shift);

        // 입력칸 안에서도 동작하는 단축키
        if ((ctrl && key is Key.F or Key.E) || (none && key == Key.F3)) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if ((ctrl && key == Key.L) || (alt && key == Key.D)) { BeginAddressEdit(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox) return;

        var inTree = IndexTree.IsKeyboardFocusWithin || PhysTree.IsKeyboardFocusWithin;
        switch (key)
        {
            case Key.Left when alt: GoBack(); break;
            case Key.Right when alt: GoForward(); break;
            case Key.Up when alt: GoUp(); break;
            case Key.Back when none: GoUp(); break;
            case Key.F5 when none: RefreshAll(); break;
            case Key.D1 when ctrl: Navigate(_lastIndexLoc); break;
            case Key.D2 when ctrl: Navigate(_lastPhysLoc); break;
            case Key.P when alt: PreviewToggle_Click(this, e); break;
            case Key.N when ctrlShift: NewFolder(); break;
            case Key.N when ctrl: OpenNewWindow(); break;
            case Key.D6 when ctrlShift: SetViewMode(false); break;
            case Key.D2 when ctrlShift: SetViewMode(true); break;
            case Key.Escape when none:
                if (SearchBox.Text.Length > 0) ClearSearch();
                else ClearCutMarks();
                break;
            case Key.F2 when none: if (inTree) RenameTreeNode(); else BeginRename(); break;
            case Key.Delete when none: if (inTree) DeleteTreeNode(); else DeleteSelected(shift: false); break;
            case Key.Delete when mods == ModifierKeys.Shift: if (!inTree) DeleteSelected(shift: true); else return; break;
            case Key.C when ctrl && !inTree: CopySelected(cut: false); break;
            case Key.X when ctrl && !inTree: CopySelected(cut: true); break;
            case Key.V when ctrl: Paste(inTree ? TreeTargetLocation() : _loc); break;
            case Key.A when ctrl && !inTree: List.SelectAll(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            OpenSelected();
            e.Handled = true;
        }
    }

    private void Item_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not ListViewItem { DataContext: ListItem item } || item.IsEditing) return;
        Open([item]);
        e.Handled = true;
    }

    private Location TreeTargetLocation()
    {
        var node = (IndexTree.IsKeyboardFocusWithin ? IndexTree.SelectedItem : PhysTree.SelectedItem) as TreeNode;
        return node != null ? LocationOf(node) : _loc;
    }
}
