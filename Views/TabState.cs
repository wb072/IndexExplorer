using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IndexExplorer.Views;

/// <summary>
/// 탭 하나 (Windows 11 탐색기처럼). 탭마다 위치·뒤로/앞으로 기록·검색어·정렬·필터·선택·스크롤을 따로 기억하고,
/// 화면(목록·트리)은 하나를 두고 탭을 바꿀 때 상태를 갈아 끼운다.
/// </summary>
public sealed partial class TabState : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private bool _isActive;

    public Location Loc { get; set; } = new(LocKind.IndexRoot);
    public List<Location> Back { get; set; } = [];      // 위에서부터 (Stack.ToList 순서)
    public List<Location> Forward { get; set; } = [];
    public Location LastIndexLoc { get; set; } = new(LocKind.IndexRoot);
    public Location LastPhysLoc { get; set; } = new(LocKind.ThisPC);
    public Location? BeforeSearch { get; set; }
    public string Search { get; set; } = "";
    public string SortKey { get; set; } = "Name";
    public bool SortDesc { get; set; }
    public (string key, bool desc)? SortBeforeRecent { get; set; }
    public Dictionary<string, HashSet<string>> Filters { get; set; } = [];
    public List<string> SelectedKeys { get; set; } = [];
    public double ScrollOffset { get; set; }
}
