using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IndexExplorer.Views;

/// <summary>
/// 크롬 탭처럼 배치한다.
/// - 자리가 넉넉하면 탭마다 MaxTabWidth
/// - 모자라면 모든 탭이 같은 비율로 좁아진다
/// - MinTabWidth 아래로는 줄이지 않고, 넘치는 부분은 Offset 만큼 밀어서 보여 준다 (좌우 화살표·휠)
/// 오른쪽에 새 탭(+) 버튼 자리(PlusReserve)를 늘 남겨 둔다.
/// </summary>
public sealed class TabStripPanel : Panel
{
    public double MaxTabWidth { get; set; } = 220;
    public double MinTabWidth { get; set; } = 96;
    public double PlusReserve { get; set; } = 40;

    /// <summary>탭 하나의 지금 폭.</summary>
    public double TabWidth { get; private set; }
    /// <summary>탭들이 실제로 차지하는 폭 (+ 버튼은 이 바로 뒤에 놓인다).</summary>
    public double UsedWidth { get; private set; }
    public bool IsOverflowing { get; private set; }
    public double Offset { get; private set; }

    /// <summary>넘침 여부·폭이 바뀌면 (화살표 표시, + 버튼 위치 갱신용).</summary>
    public event EventHandler? LayoutUpdatedInfo;

    private double _viewWidth;

    protected override Size MeasureOverride(Size available)
    {
        var n = InternalChildren.Count;
        var w = double.IsInfinity(available.Width) ? n * MaxTabWidth + PlusReserve : available.Width;
        _viewWidth = Math.Max(0, w - PlusReserve);
        var tab = n == 0 ? MaxTabWidth : Math.Clamp(_viewWidth / n, MinTabWidth, MaxTabWidth);
        var overflow = n * MinTabWidth > _viewWidth + 0.5;
        if (overflow) tab = MinTabWidth;

        double h = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(tab, available.Height));
            h = Math.Max(h, c.DesiredSize.Height);
        }
        var changed = Math.Abs(tab - TabWidth) > 0.1 || overflow != IsOverflowing;
        TabWidth = tab;
        IsOverflowing = overflow;
        UsedWidth = Math.Min(n * tab, _viewWidth);
        Offset = Math.Clamp(Offset, 0, MaxOffset);
        if (changed) Dispatcher.BeginInvoke(() => LayoutUpdatedInfo?.Invoke(this, EventArgs.Empty));
        return new Size(w, double.IsInfinity(available.Height) ? h : Math.Min(h, available.Height));
    }

    private double MaxOffset => Math.Max(0, InternalChildren.Count * TabWidth - _viewWidth);

    protected override Size ArrangeOverride(Size final)
    {
        double x = -Offset;
        foreach (UIElement c in InternalChildren)
        {
            c.Arrange(new Rect(x, 0, TabWidth, final.Height));
            x += TabWidth;
        }
        // 탭 영역 밖(+ 버튼 자리)으로 삐져나온 탭은 잘라서 숨긴다
        Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, _viewWidth), final.Height));
        Dispatcher.BeginInvoke(() => LayoutUpdatedInfo?.Invoke(this, EventArgs.Empty));
        return final;
    }

    public void ScrollBy(double tabs)
    {
        Offset = Math.Clamp(Offset + tabs * TabWidth, 0, MaxOffset);
        InvalidateArrange();
    }

    /// <summary>index 번째 탭이 보이도록.</summary>
    public void EnsureVisible(int index)
    {
        if (index < 0 || TabWidth <= 0) return;
        var left = index * TabWidth;
        var right = left + TabWidth;
        if (left < Offset) Offset = left;
        else if (right > Offset + _viewWidth) Offset = right - _viewWidth;
        Offset = Math.Clamp(Offset, 0, MaxOffset);
        InvalidateArrange();
    }

    public bool CanScrollLeft => Offset > 0.5;
    public bool CanScrollRight => Offset < MaxOffset - 0.5;
}
