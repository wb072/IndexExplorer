using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using IndexExplorer.ViewModels;

namespace IndexExplorer.Views;

/// <summary>
/// 열 필터 (탐색기와 같음): 열 제목 오른쪽 ▾ 를 누르면 그 열의 값 묶음이 나오고, 체크한 묶음만 보인다.
/// 이름은 글자 범위(ㄱ–ㄹ, A–H…), 날짜는 오늘·어제·이번 주…, 크기는 구간, 나머지는 값 그대로.
/// 여러 열의 필터는 동시에 적용(AND), 한 열 안의 체크는 하나라도 맞으면 통과(OR).
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, HashSet<string>> _filters = [];
    /// <summary>이름 열 필터의 찾기 칸 글자 (_filters 에 같이 두어 탭 저장·상태 표시·지우기를 그대로 쓴다).</summary>
    private const string NameTextKey = "NameText";

    private static readonly string[] NameOrder = ["0–9", "ㄱ–ㄹ", "ㅁ–ㅅ", "ㅇ–ㅊ", "ㅋ–ㅎ", "A–H", "I–P", "Q–Z", "기타"];
    private static readonly string[] DateOrder = ["오늘", "어제", "이번 주", "지난주", "이번 달", "지난달", "올해", "작년 이전", "날짜 없음"];
    private static readonly string[] SizeOrder =
        ["비어 있음", "아주 작음 (16KB 이하)", "작음 (16KB–1MB)", "보통 (1–128MB)", "큼 (128MB–1GB)", "매우 큼 (1–4GB)", "거대 (4GB 초과)", "크기 없음"];

    private static string NameBucket(string name)
    {
        if (name.Length == 0) return "기타";
        var c = char.ToUpperInvariant(name[0]);
        if (char.IsDigit(c)) return "0–9";
        if (c is >= 'A' and <= 'H') return "A–H";
        if (c is >= 'I' and <= 'P') return "I–P";
        if (c is >= 'Q' and <= 'Z') return "Q–Z";
        if (c is >= '가' and <= '힣')
        {
            var initial = (c - '가') / 588; // 초성 번호 (ㄱ=0 … ㅎ=18)
            return initial switch { <= 5 => "ㄱ–ㄹ", <= 10 => "ㅁ–ㅅ", <= 14 => "ㅇ–ㅊ", _ => "ㅋ–ㅎ" };
        }
        return "기타";
    }

    private static string DateBucket(DateTime? utc)
    {
        if (utc is not DateTime u) return "날짜 없음";
        var d = u.ToLocalTime().Date;
        var today = DateTime.Today;
        if (d == today) return "오늘";
        if (d == today.AddDays(-1)) return "어제";
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // 월요일 시작
        if (d >= weekStart) return "이번 주";
        if (d >= weekStart.AddDays(-7)) return "지난주";
        var monthStart = new DateTime(today.Year, today.Month, 1);
        if (d >= monthStart) return "이번 달";
        if (d >= monthStart.AddMonths(-1)) return "지난달";
        if (d.Year == today.Year) return "올해";
        return "작년 이전";
    }

    private static string SizeBucket(long? size) => size switch
    {
        null => "크기 없음",
        0 => "비어 있음",
        <= 16L << 10 => "아주 작음 (16KB 이하)",
        <= 1L << 20 => "작음 (16KB–1MB)",
        <= 128L << 20 => "보통 (1–128MB)",
        <= 1L << 30 => "큼 (128MB–1GB)",
        <= 4L << 30 => "매우 큼 (1–4GB)",
        _ => "거대 (4GB 초과)",
    };

    private static IEnumerable<string> Buckets(ListItem i, string key) => key switch
    {
        "Name" => [NameBucket(i.Name)],
        "Modified" => [DateBucket(i.Modified)],
        "Type" => [i.TypeName.Length > 0 ? i.TypeName : "유형 없음"],
        "Size" => [SizeBucket(i.Size)],
        "Location" => [i.Location.Length > 0 ? i.Location : "위치 없음"],
        "Index" => i.IndexText.Length > 0 ? i.IndexText.Split(", ") : ["미분류"],
        _ => [],
    };

    /// <summary>폴더는 이름 필터만 받는다 (날짜·크기로 폴더가 사라지면 길을 잃는다).</summary>
    private bool PassesFilter(object o)
    {
        var i = (ListItem)o;
        foreach (var (key, set) in _filters)
        {
            // 이름 필터의 찾기 칸: 이름에 그 글자가 들어 있는 것만 (폴더도)
            if (key == NameTextKey) { if (!i.Name.Contains(set.First(), StringComparison.CurrentCultureIgnoreCase)) return false; continue; }
            if (i.IsFolderLike && key != "Name") continue;
            if (!Buckets(i, key).Any(set.Contains)) return false;
        }
        return true;
    }

    private void ApplyFilter()
    {
        _view.Filter = _filters.Count == 0 ? null : PassesFilter;
        UpdateSortHeaders();
        UpdateStatus();
        UpdateEmptyState(null);
        UpdatePreview();
    }

    /// <summary>필터 창의 한 줄 (값 묶음 하나).</summary>
    public sealed class FilterRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public required string Bucket { get; init; }
        public required string Title { get; init; }
        public string Sub { get; init; } = "";
        public required string Count { get; init; }
        private bool _checked;
        public bool IsChecked { get => _checked; set => SetProperty(ref _checked, value); }
    }

    private Popup? _filterPopup;

    /// <summary>
    /// 열 필터 창. 값이 수천 개(원본 위치 등)여도 멈추지 않게 가상화 목록으로 보이는 줄만 만든다
    /// (예전 메뉴는 값마다 메뉴 항목을 만들어 8천 곳에서 2분 넘게 멈췄다). 값이 많으면 찾기 칸이 나온다.
    /// </summary>
    private void OpenFilterMenu(GridViewColumn col, FrameworkElement anchor)
    {
        if (!_colInfo.TryGetValue(col, out var info)) return;
        var key = info.key;

        var counts = new Dictionary<string, int>();
        foreach (var i in _items)
        {
            if (i.IsFolderLike && key != "Name") continue;
            if (key == "Index") { foreach (var b in Buckets(i, key).Distinct()) counts[b] = counts.GetValueOrDefault(b) + 1; }
            else { var b = Buckets(i, key).First(); counts[b] = counts.GetValueOrDefault(b) + 1; }
        }
        string[]? fixedOrder = key switch { "Name" => NameOrder, "Modified" => DateOrder, "Size" => SizeOrder, _ => null };
        List<string> ordered;
        if (fixedOrder != null) ordered = fixedOrder.Where(counts.ContainsKey).ToList();
        else
        {
            var arr = counts.Keys.ToArray();
            Array.Sort(arr, Services.NaturalComparer.Instance);
            ordered = [.. arr];
        }

        _filters.TryGetValue(key, out var current);
        var rows = ordered.Select(b =>
        {
            // 원본 위치는 폴더 이름을 앞에, 경로는 흐리게
            var isPath = key == "Location" && b.Contains('\\');
            return new FilterRow
            {
                Bucket = b, Title = isPath ? Path.GetFileName(b.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : b : b,
                Sub = isPath ? Path.GetDirectoryName(b.TrimEnd('\\')) ?? "" : "",
                Count = counts[b].ToString("N0"), IsChecked = current?.Contains(b) == true,
            };
        }).ToList();
        var view = new ListCollectionView(rows);

        var list = new ListBox
        {
            ItemsSource = view, MaxHeight = 380, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("InkBrush"), Padding = new Thickness(0, 0, 4, 0),
            ItemTemplate = (DataTemplate)FindResource("FilterRowTemplate"),
            ItemContainerStyle = (Style)FindResource("PlainListItem"),
            Focusable = false,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);

        void Toggle(FilterRow row)
        {
            if (!_filters.TryGetValue(key, out var set)) _filters[key] = set = [];
            if (row.IsChecked) set.Add(row.Bucket); else set.Remove(row.Bucket);
            if (set.Count == 0) _filters.Remove(key);
            ApplyFilter();
        }
        list.AddHandler(System.Windows.Controls.Primitives.ToggleButton.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is FilterRow row) Toggle(row);
        }));
        // 줄 아무 곳이나 눌러도 체크
        list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindAncestor<CheckBox>(d) == null && FindAncestor<ListBoxItem>(d)?.DataContext is FilterRow row)
            {
                row.IsChecked = !row.IsChecked;
                Toggle(row);
            }
        };

        var panel = new StackPanel { Width = 340 };
        var isName = key == "Name";
        if (isName || rows.Count > 12)
        {
            // 이름: 글자로 목록을 바로 거른다 / 값이 많은 열(원본 위치 등): 이 창의 값 목록을 거른다
            var text = isName && _filters.TryGetValue(NameTextKey, out var nt) ? nt.First() : "";
            var search = new TextBox
            {
                Style = (Style)FindResource("FormBox"), Margin = new Thickness(8, 6, 8, 4), Padding = new Thickness(4, 0, 4, 0),
                MinHeight = 0, Height = 26, VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 12.5, Text = text,
            };
            var hint = new TextBlock
            {
                Text = isName ? "이름에 들어간 글자로 찾기" : $"{rows.Count:N0}개 중 찾기", FontSize = 12.5,
                Foreground = (Brush)FindResource("MutedBrush"), IsHitTestVisible = false,
                Margin = new Thickness(19, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
            };
            search.TextChanged += (_, _) =>
            {
                var q = search.Text.Trim();
                hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (isName)
                {
                    if (q.Length == 0) _filters.Remove(NameTextKey); else _filters[NameTextKey] = [q];
                    ApplyFilter();
                }
                else view.Filter = q.Length == 0 ? null : o => ((FilterRow)o).Bucket.Contains(q, StringComparison.CurrentCultureIgnoreCase);
            };
            search.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) _filterPopup!.IsOpen = false; };
            var g = new Grid();
            g.Children.Add(search);
            g.Children.Add(hint);
            panel.Children.Add(g);
            panel.Loaded += (_, _) => { search.Focus(); search.CaretIndex = search.Text.Length; };
        }
        if (current != null || (isName && _filters.ContainsKey(NameTextKey)))
        {
            var clear = new Button { Style = (Style)FindResource("ToolButtonB"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Height = 28, Margin = new Thickness(4, 2, 4, 0) };
            clear.Content = new TextBlock { Text = "필터 지우기", Foreground = (Brush)FindResource("AccentBrush") };
            clear.Click += (_, _) => { _filters.Remove(key); if (isName) _filters.Remove(NameTextKey); ApplyFilter(); _filterPopup!.IsOpen = false; };
            panel.Children.Add(clear);
        }
        if (rows.Count == 0) panel.Children.Add(new TextBlock { Text = "항목 없음", Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(14, 10, 14, 10) });
        else panel.Children.Add(list);

        var border = new Border
        {
            Background = Brushes.White, BorderBrush = (Brush)FindResource("HairlineBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(0, 2, 0, 4), Margin = new Thickness(8, 2, 8, 10), Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.12, Color = Colors.Black },
        };
        _filterPopup = new Popup
        {
            Child = border, PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, HorizontalOffset = -8,
        };
        border.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) _filterPopup.IsOpen = false; };
        _filterPopup.IsOpen = true;
    }

    internal int TestFilterRowsMade() =>
        _filterPopup?.Child is { } c ? CountDescendants<ListBoxItem>(c) : 0;
    internal void TestCloseFilter() { if (_filterPopup != null) _filterPopup.IsOpen = false; }

    private TextBox? FilterSearchBox() => _filterPopup?.Child is { } c ? FindDescendant<TextBox>(c) : null;
    internal double TestFilterSearchHeight() => FilterSearchBox()?.ActualHeight ?? 0;
    internal void TestFilterType(string text) { if (FilterSearchBox() is { } b) b.Text = text; }

    internal void TestShotFilter(string name)
    {
        if (_filterPopup?.Child is FrameworkElement fe) ShotElement(fe, name);
    }

    /// <summary>자체 검증: 떠 있는 창(필터·드롭다운 등) 하나를 PNG로.</summary>
    private static void ShotElement(FrameworkElement fe, string name)
    {
        var dpi = VisualTreeHelper.GetDpi(fe);
        var size = new Rect(0, 0, fe.ActualWidth, fe.ActualHeight);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, size);
            dc.DrawRectangle(new VisualBrush(fe), null, size);
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(size.Width * dpi.DpiScaleX), (int)(size.Height * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(App.SelfTestOut!, name + ".png"));
        enc.Save(fs);
    }

    private static int CountDescendants<T>(DependencyObject d) where T : DependencyObject
    {
        int n = 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T) n++;
            n += CountDescendants<T>(c);
        }
        return n;
    }
}
