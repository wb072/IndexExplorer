using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    private void OpenFilterMenu(GridViewColumn col, FrameworkElement anchor)
    {
        if (!_colInfo.TryGetValue(col, out var info)) return;
        var key = info.key;

        var counts = new Dictionary<string, int>();
        foreach (var i in _items)
        {
            if (i.IsFolderLike && key != "Name") continue;
            foreach (var b in Buckets(i, key).Distinct()) counts[b] = counts.GetValueOrDefault(b) + 1;
        }
        string[]? fixedOrder = key switch { "Name" => NameOrder, "Modified" => DateOrder, "Size" => SizeOrder, _ => null };
        var ordered = fixedOrder != null
            ? fixedOrder.Where(counts.ContainsKey).ToList()
            : counts.Keys.Order(Services.NaturalComparer.Instance).ToList();

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom, MaxHeight = 460 };
        _filters.TryGetValue(key, out var current);
        if (current != null)
        {
            var clear = new MenuItem { Header = "필터 지우기" };
            clear.Click += (_, _) => { _filters.Remove(key); ApplyFilter(); };
            menu.Items.Add(clear);
            menu.Items.Add(new Separator());
        }
        if (ordered.Count == 0) menu.Items.Add(new MenuItem { Header = "항목 없음", IsEnabled = false });
        foreach (var b in ordered)
        {
            var bucket = b;
            var mi = new MenuItem
            {
                Header = bucket, InputGestureText = counts[bucket].ToString("N0"),
                IsCheckable = true, IsChecked = current?.Contains(bucket) == true, StaysOpenOnClick = true,
            };
            mi.Click += (_, _) =>
            {
                if (!_filters.TryGetValue(key, out var set)) _filters[key] = set = [];
                if (mi.IsChecked) set.Add(bucket); else set.Remove(bucket);
                if (set.Count == 0) _filters.Remove(key);
                ApplyFilter();
            };
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }
}
