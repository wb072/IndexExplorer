using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IndexExplorer.ViewModels;

namespace IndexExplorer.Views;

/// <summary>
/// 끌어서 여러 개 선택 (탐색기의 범위 선택).
/// - 빈 곳에서 끌거나, 자세히 보기에서 이름 글자 밖(날짜·유형 칸)에서 끌면 파란 사각형으로 선택
/// - Ctrl 을 누르고 끌면 기존 선택에 더한다
/// - 목록 위·아래 끝으로 끌면 자동으로 스크롤
/// - 가상화된 긴 목록도 줄 높이로 계산해, 화면 밖으로 지나간 항목까지 정확히 선택
/// </summary>
public partial class MainWindow
{
    private bool _marqueeArmed, _marqueeActive, _marqueeUpdating;
    private Point _marqueeStart;                 // 목록 내용 좌표 (스크롤 포함)
    private double _mStartRow;                   // 자세히 보기: 시작점이 몇 번째 줄의 어디쯤인가 (가상화·스크롤에도 흔들리지 않음)
    private HashSet<ListItem> _marqueeBase = [];
    private ScrollViewer? _mSv;
    private ScrollContentPresenter? _mScp;
    private DispatcherTimer? _marqueeTimer;
    private ListViewItem? _rowClick;
    private ListItem? _anchor;
    private HashSet<ListItem> _marqueeSticky = [];

    /// <summary>화면에 만들어져 있는 목록 칸 전부 (묶음 안에 든 것 포함).</summary>
    private IEnumerable<ListViewItem> RealizedContainers()
    {
        if (_mScp == null) yield break;
        var stack = new Stack<DependencyObject>();
        stack.Push(_mScp);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is ListViewItem lvi) yield return lvi;
                else stack.Push(c);
            }
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindDescendant<T>(c) is { } found) return found;
        }
        return null;
    }

    /// <summary>자세히 보기에서 이름 칸의 아이콘·글자를 눌렀는가 (그 밖은 범위 선택 시작 지점).</summary>
    private static bool IsOnNameContent(DependencyObject? src, ListViewItem container)
    {
        if (src is not (TextBlock or Image or System.Windows.Documents.Run)) return false;
        for (var d = src; d != null && d != container; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { Tag: "name" }) return true;
        return false;
    }

    private bool FindListScroll()
    {
        _mSv = FindDescendant<ScrollViewer>(List);
        if (_mSv == null) return false;
        _mScp = _mSv.Template.FindName("PART_ScrollContentPresenter", _mSv) as ScrollContentPresenter
                ?? FindDescendant<ScrollContentPresenter>(_mSv);
        return _mScp != null;
    }

    private bool ArmMarquee(MouseButtonEventArgs e) => FindListScroll() && ArmMarqueeAt(e.GetPosition(_mScp));

    /// <param name="p">목록 화면(보이는 영역) 기준 위치</param>
    private bool ArmMarqueeAt(Point p)
    {
        if (_mSv == null || _mScp == null) return false;
        _marqueeStart = new Point(p.X + _mSv.HorizontalOffset, p.Y + _mSv.VerticalOffset);
        if (List.View != null && !IsGrouped) _mStartRow = RowPosAt(p.Y) ?? 0;
        _marqueeBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? List.SelectedItems.Cast<ListItem>().ToHashSet() : [];
        _marqueeSticky = [];
        _marqueeArmed = true;
        return true;
    }

    private void StartMarquee()
    {
        if (_mScp == null) return;
        _marqueeActive = true;
        List.CaptureMouse();
        var origin = _mScp.TranslatePoint(new Point(0, 0), MarqueeLayer);
        MarqueeLayer.Clip = new RectangleGeometry(new Rect(origin, _mScp.RenderSize));
        MarqueeRect.Visibility = Visibility.Visible;
        _marqueeTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => AutoScrollMarquee(), Dispatcher);
        _marqueeTimer.Start();
    }

    private void EndMarquee()
    {
        var wasActive = _marqueeActive;
        _marqueeArmed = false;
        _marqueeActive = false;
        _marqueeTimer?.Stop();
        MarqueeRect.Visibility = Visibility.Collapsed;
        if (List.IsMouseCaptured) List.ReleaseMouseCapture();
        if (wasActive)
        {
            UpdateStatus();
            UpdatePreview();
            UpdateCommandStates();
        }
    }

    private void List_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_marqueeActive) EndMarquee();
    }

    private void AutoScrollMarquee()
    {
        if (!_marqueeActive || _mSv == null || _mScp == null) return;
        var p = Mouse.GetPosition(_mScp);
        double dy = p.Y < 0 ? Math.Max(-40, p.Y / 2 - 4) : p.Y > _mScp.ActualHeight ? Math.Min(40, (p.Y - _mScp.ActualHeight) / 2 + 4) : 0;
        if (dy != 0)
        {
            _mSv.ScrollToVerticalOffset(_mSv.VerticalOffset + dy);
            _mSv.UpdateLayout();
            UpdateMarquee();
        }
    }

    private void UpdateMarquee(Point? at = null)
    {
        if (_mSv == null || _mScp == null) return;
        var p = at ?? Mouse.GetPosition(_mScp);
        var hits = new HashSet<ListItem>(_marqueeBase);
        var n = List.Items.Count;
        var startX = _marqueeStart.X - _mSv.HorizontalOffset;
        double startY;

        if (List.View != null && !IsGrouped)
        {
            // 자세히 보기: 시작 줄 ~ 지금 줄 (화면 밖으로 지나간 줄 포함)
            var curRow = RowPosAt(p.Y) ?? 0;
            var a = Math.Min(_mStartRow, curRow);
            var b = Math.Max(_mStartRow, curRow);
            var first = Math.Max(0, (int)Math.Floor(a));
            var last = Math.Min(n - 1, (int)Math.Floor(b));
            for (int i = first; i <= last; i++) hits.Add((ListItem)List.Items[i]);
            startY = ViewportYOfRow(_mStartRow) ?? p.Y;
        }
        else
        {
            // 큰 아이콘 보기·묶어 보기: 화면에 그려진 칸의 실제 위치로 (묶음 머리줄이 끼어 있어도 정확)
            // 스크롤로 화면 밖에 나간 칸은 이전 판정을 유지한다
            startY = _marqueeStart.Y - _mSv.VerticalOffset;
            var r = new Rect(new Point(startX, startY), p);
            var realized = new HashSet<ListItem>();
            foreach (var c in RealizedContainers())
            {
                if (c.DataContext is not ListItem item || c.ActualWidth == 0 || !c.IsVisible) continue;
                realized.Add(item);
                var inset = List.View == null ? 3 : 0;
                var pt = c.TranslatePoint(new Point(inset, inset), _mScp);
                if (new Rect(pt, new Size(Math.Max(0, c.ActualWidth - 2 * inset), Math.Max(0, c.ActualHeight - 2 * inset))).IntersectsWith(r))
                    hits.Add(item);
            }
            foreach (var s in _marqueeSticky) if (!realized.Contains(s)) hits.Add(s);
            _marqueeSticky = hits.Except(_marqueeBase).ToHashSet();
        }

        // 사각형 그리기 (화면 좌표)
        var rect = new Rect(new Point(startX, startY), p);
        var tl = _mScp.TranslatePoint(rect.TopLeft, MarqueeLayer);
        Canvas.SetLeft(MarqueeRect, tl.X);
        Canvas.SetTop(MarqueeRect, tl.Y);
        MarqueeRect.Width = Math.Max(1, rect.Width);
        MarqueeRect.Height = Math.Max(1, rect.Height);
        ApplyMarqueeSelection(hits);
    }

    /// <summary>화면에 그려진 줄들 (번호, 화면 y, 높이).</summary>
    private List<(int idx, double y, double h)> VisibleRows()
    {
        var rows = new List<(int, double, double)>();
        if (_mScp == null) return rows;
        var panel = FindDescendant<VirtualizingStackPanel>(_mScp);
        if (panel == null) return rows;
        foreach (UIElement child in panel.Children)
        {
            if (child is not ListViewItem c || c.ActualHeight <= 0 || !c.IsVisible) continue;
            var idx = List.ItemContainerGenerator.IndexFromContainer(c);
            if (idx < 0) continue;
            var y = c.TranslatePoint(new Point(0, 0), _mScp).Y;
            if (y < -c.ActualHeight * 2 || y > _mScp.ActualHeight + c.ActualHeight) continue;
            rows.Add((idx, y, c.ActualHeight));
        }
        return rows;
    }

    /// <summary>화면 y 위치가 몇 번째 줄의 어디쯤인가 (예: 4.5 = 다섯째 줄 가운데). 빈 목록이면 null.</summary>
    private double? RowPosAt(double vy)
    {
        var rows = VisibleRows();
        if (rows.Count == 0) return List.Items.Count == 0 ? 0 : null;
        foreach (var (idx, y, h) in rows)
            if (vy >= y && vy < y + h) return idx + (vy - y) / h;
        var top = rows.MinBy(r => r.y);
        var bottom = rows.MaxBy(r => r.y);
        return vy < top.y
            ? top.idx - (top.y - vy) / top.h
            : bottom.idx + 1 + (vy - (bottom.y + bottom.h)) / bottom.h;
    }

    /// <summary>줄 위치(RowPosAt 값)의 지금 화면 y — 화면 밖이면 가까운 줄에서 이어 계산.</summary>
    private double? ViewportYOfRow(double pos)
    {
        var rows = VisibleRows();
        if (rows.Count == 0) return null;
        var near = rows.MinBy(r => Math.Abs(r.idx - pos));
        return near.y + (pos - near.idx) * near.h;
    }

    /// <summary>자세히 보기의 (줄 높이, 첫 줄 위치) — 자체 검증용.</summary>
    private (double h, double origin)? RowGeometry()
    {
        var rows = VisibleRows();
        if (rows.Count == 0 || _mSv == null) return null;
        var r = rows.MinBy(x => x.idx);
        return (r.h, r.y + _mSv.VerticalOffset - r.idx * r.h);
    }

    private void ApplyMarqueeSelection(HashSet<ListItem> target)
    {
        _marqueeUpdating = true;
        try
        {
            var current = List.SelectedItems.Cast<ListItem>().ToHashSet();
            if (!current.SetEquals(target)) List.SelectMany(target.ToList()); // 한 번에 (수만 개도 즉시)
        }
        finally { _marqueeUpdating = false; }
        UpdateStatus();
    }

    /// <summary>자세히 보기에서 이름 밖을 눌렀다 뗐을 때: 탐색기처럼 그 줄 선택 (Ctrl 토글, Shift 범위).</summary>
    private void RowClickSelect(ListViewItem container)
    {
        if (container.DataContext is not ListItem item) return;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control))
        {
            if (List.SelectedItems.Contains(item)) List.SelectedItems.Remove(item);
            else List.SelectedItems.Add(item);
            _anchor = item;
        }
        else if (mods.HasFlag(ModifierKeys.Shift) && _anchor != null && List.Items.IndexOf(_anchor) is var a and >= 0)
        {
            var b = List.Items.IndexOf(item);
            var range = new List<object>();
            for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) range.Add(List.Items[i]);
            List.SelectMany(range);
        }
        else
        {
            List.SelectedItems.Clear();
            List.SelectedItems.Add(item);
            _anchor = item;
        }
        container.Focus();
    }
}
