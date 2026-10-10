using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using IndexExplorer.Native;
using IndexExplorer.ViewModels;

namespace IndexExplorer.Views;

public enum GroupBy { None, Folder, Date, Type }

/// <summary>
/// 묶어 보기 (탐색기의 그룹화): 폴더별 · 수정한 날짜별 · 유형별.
/// 묶음 순서는 정렬을 따른다 — 최근 순이면 최근에 바뀐 폴더부터.
/// 파일 종류 보기는 폴더별이 기본(흩어진 파일이 어디에 몰려 있는지 한눈에), 다른 보기는 묶지 않는다.
/// </summary>
public partial class MainWindow
{
    private GroupBy _groupBy;
    /// <summary>파일 종류 보기에서 마지막으로 고른 묶음 (창끼리 공유).</summary>
    private static GroupBy _kindGroupPref = GroupBy.Folder;

    private sealed class DateGroup : GroupDescription
    {
        public override object GroupNameFromItem(object item, int level, CultureInfo culture) =>
            DateBucket(((ListItem)item).Modified);
    }

    private GroupBy DefaultGroupFor(LocKind k) => k == LocKind.FileKind ? _kindGroupPref : GroupBy.None;

    private bool IsGrouped => _view.GroupDescriptions.Count > 0;

    private void ApplyGrouping()
    {
        if (Resources["GroupFolderIcon"] == null) Resources["GroupFolderIcon"] = ShellImages.Icon(System.IO.Path.GetTempPath(), true);
        _view.GroupDescriptions.Clear();
        GroupDescription? g = _groupBy switch
        {
            GroupBy.Folder => new PropertyGroupDescription(nameof(ListItem.Location)),
            GroupBy.Date => new DateGroup(),
            GroupBy.Type => new PropertyGroupDescription(nameof(ListItem.TypeName)),
            _ => null,
        };
        if (g != null) _view.GroupDescriptions.Add(g);
    }

    private void SetGroupBy(GroupBy g)
    {
        _groupBy = g;
        if (_loc.Kind == LocKind.FileKind) _kindGroupPref = g;
        ApplyGrouping();
        UpdateColumns();
        UpdateStatus();
    }

    /// <summary>정렬 메뉴·빈 곳 우클릭 메뉴에 들어가는 "묶어 보기".</summary>
    private MenuItem GroupMenu()
    {
        var root = new MenuItem { Header = "묶어 보기" };
        foreach (var (g, title) in new[] { (GroupBy.None, "묶지 않음"), (GroupBy.Folder, "폴더별"), (GroupBy.Date, "수정한 날짜별"), (GroupBy.Type, "유형별") })
        {
            var mi = new MenuItem { Header = title, IsCheckable = true, IsChecked = _groupBy == g };
            mi.Click += (_, _) => SetGroupBy(g);
            root.Items.Add(mi);
        }
        return root;
    }

    /// <summary>머리줄 아무 곳이나 누르면 접고 편다 (열기 버튼·화살표 자체는 제외).</summary>
    private void GroupHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && (FindAncestor<Button>(d) != null || FindAncestor<ToggleButton>(d) != null)) return;
        if (sender is FrameworkElement hdr && FindDescendant<ToggleButton>(hdr) is { } t) t.IsChecked = t.IsChecked != true;
        e.Handled = true;
    }

    private void GroupOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string dir } && System.IO.Directory.Exists(dir))
            Navigate(new Location(LocKind.Dir, Path: dir));
        e.Handled = true;
    }

    /// <summary>상태 표시줄용: 묶음 수 (예: "폴더 12곳").</summary>
    private string GroupSummary() => !IsGrouped || _view.Groups == null ? "" : _groupBy switch
    {
        GroupBy.Folder => $"     폴더 {_view.Groups.Count:N0}곳",
        GroupBy.Date => $"     날짜 묶음 {_view.Groups.Count:N0}개",
        GroupBy.Type => $"     유형 {_view.Groups.Count:N0}가지",
        _ => "",
    };
}
