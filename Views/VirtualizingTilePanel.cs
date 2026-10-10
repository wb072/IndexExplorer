using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace IndexExplorer.Views;

/// <summary>
/// 큰 아이콘 보기용 가상화 배치: 같은 크기의 칸을 줄줄이 놓되, 화면에 보이는 줄(+위아래 한 줄)만 만든다.
/// 항목이 수만 개여도 만들어지는 칸은 수십 개라 열기·스크롤이 즉시다. (WrapPanel 은 전부 만든다)
/// </summary>
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingTilePanel), new FrameworkPropertyMetadata(106.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingTilePanel), new FrameworkPropertyMetadata(110.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    private Size _extent, _viewport;
    private Point _offset;
    private int _columns = 1;
    private readonly TranslateTransform _scroll = new();

    public VirtualizingTilePanel() { RenderTransform = _scroll; }

    /// <summary>이 칸 묶음이 맡은 항목 수 (묶어 보기면 그 묶음 안의 수).</summary>
    private int ItemCount => (ItemContainerGenerator as ItemContainerGenerator)?.Items.Count ?? ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    protected override Size MeasureOverride(Size available)
    {
        // VirtualizingPanel 은 InternalChildren 을 한 번 건드려야 ItemContainerGenerator 가 연결된다
        _ = InternalChildren;
        var width = double.IsInfinity(available.Width) ? ItemWidth * 8 : available.Width;
        _columns = Math.Max(1, (int)(width / ItemWidth));
        var count = ItemCount;
        var rows = (count + _columns - 1) / _columns;
        // 스크롤을 직접 맡을 때만 가상화. 묶어 보기의 묶음 안에서는 그 묶음 칸을 모두 그린다 (높이 = 줄 수)
        var virtualize = ScrollOwner != null && !double.IsInfinity(available.Height);
        var height = virtualize ? available.Height : rows * ItemHeight;
        if (virtualize) UpdateScrollInfo(new Size(width, rows * ItemHeight + 12), new Size(width, height));
        else _offset.Y = 0;

        // 보이는 줄 범위 (+위아래 한 줄 여유)
        var firstRow = virtualize ? Math.Max(0, (int)(_offset.Y / ItemHeight) - 1) : 0;
        var lastRow = virtualize ? Math.Min(rows - 1, (int)((_offset.Y + height) / ItemHeight) + 1) : rows - 1;
        var first = firstRow * _columns;
        var last = Math.Min(count - 1, (lastRow + 1) * _columns - 1);

        var gen = ItemContainerGenerator;
        if (count > 0 && last >= first && gen != null)
        {
            var start = gen.GeneratorPositionFromIndex(first);
            var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (gen.StartAt(start, GeneratorDirection.Forward, true))
            {
                for (int i = first; i <= last; i++, childIndex++)
                {
                    var child = (UIElement)gen.GenerateNext(out var isNew);
                    if (isNew)
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        gen.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(ItemWidth, ItemHeight));
                }
            }
        }
        CleanUp(first, last);
        return new Size(width, height);
    }

    /// <summary>보이지 않게 된 칸은 버린다.</summary>
    private void CleanUp(int first, int last)
    {
        var gen = ItemContainerGenerator;
        if (gen == null) return;
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var pos = new GeneratorPosition(i, 0);
            var itemIndex = gen.IndexFromGeneratorPosition(pos);
            if (itemIndex < first || itemIndex > last)
            {
                // 키보드 포커스가 있는 칸은 남긴다 (화살표로 이동 중)
                if (InternalChildren[i].IsKeyboardFocusWithin) continue;
                gen.Remove(pos, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override Size ArrangeOverride(Size final)
    {
        var gen = ItemContainerGenerator;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var itemIndex = gen?.IndexFromGeneratorPosition(new GeneratorPosition(i, 0)) ?? i;
            var row = itemIndex / _columns;
            var col = itemIndex % _columns;
            InternalChildren[i].Arrange(new Rect(col * ItemWidth, row * ItemHeight, ItemWidth, ItemHeight));
        }
        _scroll.Y = -_offset.Y;
        return final;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        if (args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace or NotifyCollectionChangedAction.Move)
            RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
        else if (args.Action == NotifyCollectionChangedAction.Reset)
            RemoveInternalChildRange(0, InternalChildren.Count);
        InvalidateMeasure();
    }

    /// <summary>키보드로 옮겨 간 항목이 보이도록 (ListView 가 부른다).</summary>
    protected override void BringIndexIntoView(int index)
    {
        var row = index / Math.Max(1, _columns);
        var top = row * ItemHeight;
        if (top < _offset.Y) SetVerticalOffset(top);
        else if (top + ItemHeight > _offset.Y + _viewport.Height) SetVerticalOffset(top + ItemHeight - _viewport.Height);
    }

    // ===================== IScrollInfo =====================

    private void UpdateScrollInfo(Size extent, Size viewport)
    {
        if (extent != _extent || viewport != _viewport)
        {
            _extent = extent;
            _viewport = viewport;
            _offset.Y = Math.Clamp(_offset.Y, 0, Math.Max(0, _extent.Height - _viewport.Height));
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(_offset.Y - 24);
    public void LineDown() => SetVerticalOffset(_offset.Y + 24);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - ItemHeight * 0.75);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + ItemHeight * 0.75);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (Math.Abs(offset - _offset.Y) < 0.1) return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            if (!ReferenceEquals(InternalChildren[i], visual) && !InternalChildren[i].IsAncestorOf(visual)) continue;
            var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            BringIndexIntoView(itemIndex);
            break;
        }
        return rectangle;
    }
}
