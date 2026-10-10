using System.Collections;
using System.Windows.Controls;

namespace IndexExplorer.Views;

/// <summary>
/// 여러 항목을 한 번에 선택할 수 있는 ListView.
/// SelectedItems.Add 를 수천 번 부르면 선택이 바뀔 때마다 화면 갱신이 일어나 수만 개에서 몇 분씩 걸린다.
/// SetSelectedItems 는 한 번의 선택 변경으로 끝낸다.
/// </summary>
public sealed class FastListView : ListView
{
    public void SelectMany(IEnumerable items)
    {
        var list = items as IList ?? items.Cast<object>().ToList();
        // 먼저 한 번에 비운다: 많이 선택된 상태에서 바로 바꾸면 하나씩 비교해 수만 개에서 십수 초 걸린다
        if (SelectedItems.Count > 0) UnselectAll();
        if (list.Count == 0) return;
        SetSelectedItems(list);
    }
}
