using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IndexExplorer.Native;
using IndexExplorer.ViewModels;

namespace IndexExplorer.Views;

/// <summary>
/// 탭 (Windows 11 탐색기와 같음): Ctrl+T 새 탭, Ctrl+W 닫기, Ctrl+Tab 다음 탭, 가운데 버튼으로 닫기/새 탭에서 열기,
/// 파일을 끌고 탭 위에 잠시 머무르면 그 탭으로 바뀌어 다른 탭으로 옮기거나 복사할 수 있다.
/// 맨 위 탭 줄은 창 제목 표시줄을 겸한다 (끌어서 창 이동, 두 번 눌러 최대화).
/// </summary>
public partial class MainWindow
{
    private readonly ObservableCollection<TabState> _tabs = [];
    private TabState _tab = null!;
    private bool _restoringTab;

    private void InitTabs()
    {
        _tab = new TabState { IsActive = true };
        _tabs.Add(_tab);
        TabItems.ItemsSource = _tabs;
        StateChanged += (_, _) => ApplyWindowFrame();
        ApplyWindowFrame();
    }

    // ----- 상태 저장 / 복원 -----

    private void SaveTab(TabState t)
    {
        t.Loc = _loc;
        t.Back = _back.ToList();
        t.Forward = _fwd.ToList();
        t.LastIndexLoc = _lastIndexLoc;
        t.LastPhysLoc = _lastPhysLoc;
        t.BeforeSearch = _beforeSearch;
        t.Search = SearchBox.Text;
        t.SortKey = _sortKey;
        t.SortDesc = _sortDesc;
        t.SortBeforeRecent = _sortBeforeRecent;
        t.Filters = _filters.ToDictionary(kv => kv.Key, kv => kv.Value.ToHashSet());
        t.SelectedKeys = Selected().Select(i => i.Key).ToList();
        t.ScrollOffset = FindDescendant<ScrollViewer>(List)?.VerticalOffset ?? 0;
    }

    private void RestoreTab(TabState t)
    {
        _back.Clear();
        foreach (var l in Enumerable.Reverse(t.Back)) _back.Push(l);
        _fwd.Clear();
        foreach (var l in Enumerable.Reverse(t.Forward)) _fwd.Push(l);
        _lastIndexLoc = t.LastIndexLoc;
        _lastPhysLoc = t.LastPhysLoc;
        _sortKey = t.SortKey;
        _sortDesc = t.SortDesc;
        _sortBeforeRecent = t.SortBeforeRecent;

        _restoringTab = true;
        try
        {
            if (t.Loc.Kind == LocKind.Search) SetSearchText(t.Search);
            Navigate(t.Loc, record: false);
        }
        finally { _restoringTab = false; }

        _beforeSearch = t.BeforeSearch;
        foreach (var (k, v) in t.Filters) _filters[k] = v.ToHashSet();
        if (_filters.Count > 0) ApplyFilter();
        UpdateSortHeaders();
        UpdateNavButtons();
        if (t.SelectedKeys.Count > 0) SelectKeys(t.SelectedKeys);
        var offset = t.ScrollOffset;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FindDescendant<ScrollViewer>(List)?.ScrollToVerticalOffset(offset));
    }

    private void SwitchTo(TabState t)
    {
        if (t == _tab || !_tabs.Contains(t)) return;
        CommitOrCancelEdits();
        SaveTab(_tab);
        _tab.IsActive = false;
        _tab = t;
        t.IsActive = true;
        RestoreTab(t);
    }

    private void CommitOrCancelEdits() => CancelAllEdits();

    /// <summary>새 탭. 위치를 주지 않으면 색인 첫 화면 (탐색기의 "홈"에 해당).</summary>
    private void NewTab(Location? at = null)
    {
        SaveTab(_tab);
        var t = new TabState { Loc = at ?? new Location(LocKind.IndexRoot) };
        if (t.Loc.IsPhysical) t.LastPhysLoc = t.Loc;
        else if (t.Loc.Kind != LocKind.Search) t.LastIndexLoc = t.Loc;
        _tabs.Insert(_tabs.IndexOf(_tab) + 1, t);
        _tab.IsActive = false;
        _tab = t;
        t.IsActive = true;
        RestoreTab(t);
    }

    private void CloseTab(TabState t)
    {
        if (_tabs.Count == 1) { Close(); return; }
        var idx = _tabs.IndexOf(t);
        _tabs.Remove(t);
        if (t != _tab) return;
        var next = _tabs[Math.Min(idx, _tabs.Count - 1)];
        _tab = next;
        next.IsActive = true;
        RestoreTab(next);
    }

    private void CycleTab(int step)
    {
        if (_tabs.Count < 2) return;
        var idx = (_tabs.IndexOf(_tab) + step + _tabs.Count) % _tabs.Count;
        SwitchTo(_tabs[idx]);
    }

    /// <summary>주소 표시줄을 고칠 때 탭 제목·아이콘도 함께.</summary>
    private void UpdateTabHeader(string title)
    {
        if (_tab == null) return;
        _tab.Title = title;
        _tab.Icon = _loc.Kind switch
        {
            LocKind.IndexRoot => (ImageSource)FindResource("IconIndexRoot"),
            LocKind.IndexFolder => ShellImages.Icon(Services.Settings.DataDir, true),
            LocKind.Recent => (ImageSource)FindResource("IconRecent"),
            LocKind.Inbox => (ImageSource)FindResource("IconInbox"),
            LocKind.AllFiles or LocKind.Search => (ImageSource)FindResource("IconAllFiles"),
            LocKind.ThisPC => ShellImages.Icon("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", true, perPath: true),
            _ => ShellImages.Icon(_loc.Path, true, perPath: true),
        };
    }

    // ----- 탭 줄 이벤트 -----

    private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabState t } && e.ChangedButton == MouseButton.Left)
        {
            SwitchTo(t);
            e.Handled = true;
        }
    }

    private void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: TabState t })
        {
            CloseTab(t);
            e.Handled = true;
        }
    }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabState t }) CloseTab(t);
        e.Handled = true;
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => NewTab();

    // 끌고 있는 파일을 탭 위에 잠시 머무르면 그 탭으로 (다른 탭으로 옮기기·복사)
    private TabState? _dragHoverTab;
    private DateTime _dragHoverSince;

    private void Tab_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: TabState t } || t == _tab) { _dragHoverTab = null; return; }
        if (t != _dragHoverTab) { _dragHoverTab = t; _dragHoverSince = DateTime.Now; return; }
        if ((DateTime.Now - _dragHoverSince).TotalMilliseconds > 500) { SwitchTo(t); _dragHoverTab = null; }
    }

    // ----- 창 틀 (제목 표시줄 대신 탭 줄) -----

    private void ApplyWindowFrame()
    {
        // 최대화하면 창이 화면보다 조금 크게 잡히므로 그만큼 안쪽으로
        var max = WindowState == WindowState.Maximized;
        var t = SystemParameters.WindowResizeBorderThickness;
        RootFrame.Padding = max ? new Thickness(t.Left + 4, t.Top + 4, t.Right + 4, t.Bottom + 4) : new Thickness(0);
        RootFrame.BorderThickness = max ? new Thickness(0) : new Thickness(1);
        MaxGlyph.Text = max ? "" : "";
        MaxBtn.ToolTip = max ? "이전 크기로 복원" : "최대화";
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWin_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>가운데 버튼: 폴더는 새 탭에서 열기, 탭은 닫기.</summary>
    private bool HandleMiddleClick(MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (FindAncestor<ListViewItem>(src)?.DataContext is ListItem { IsFolderLike: true } li)
        {
            NewTab(li.Kind == ItemKind.IndexFolder ? new Location(LocKind.IndexFolder, li.FolderId) : new Location(LocKind.Dir, Path: li.Path));
            return true;
        }
        if (FindAncestor<TreeViewItem>(src)?.DataContext is TreeNode node && node != TreeNode.Placeholder)
        {
            NewTab(LocationOf(node));
            return true;
        }
        return false;
    }
}
