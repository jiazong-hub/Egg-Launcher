using System.Windows;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace Launcher.App;

/// <summary>Fits mode cards without a trailing gutter and preserves their proportions.</summary>
public sealed class ModeCardPanel : Panel
{
    internal const double CardWidth = 390;
    internal const double Gap = 14;
    internal const double TwoColumnWidth = CardWidth * 2 + Gap;

    private readonly List<double> _rowHeights = [];
    private int _columns = 1;
    private double _cardWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width)
            ? TwoColumnWidth
            : Math.Max(0, availableSize.Width);
        _columns = Math.Max(1, (int)Math.Floor((width + Gap) / (CardWidth + Gap)));
        _cardWidth = Math.Min(width, CardWidth);
        _rowHeights.Clear();
        var index = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(_cardWidth, double.PositiveInfinity));
            var row = index / _columns;
            if (row == _rowHeights.Count) _rowHeights.Add(0);
            _rowHeights[row] = Math.Max(_rowHeights[row], child.DesiredSize.Height);
            index++;
        }

        return new Size(width, _rowHeights.Sum() + Math.Max(0, _rowHeights.Count - 1) * Gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var index = 0;
        var top = 0d;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var row = index / _columns;
            var height = row < _rowHeights.Count ? _rowHeights[row] : child.DesiredSize.Height;
            child.Arrange(new Rect(index % _columns * (_cardWidth + Gap), top, _cardWidth, height));
            index++;
            if (index % _columns == 0) top += height + Gap;
        }
        return finalSize;
    }
}
