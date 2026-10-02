using System.Windows;
using System.Windows.Controls;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace Launcher.App;

/// <summary>Pairs visible parameter fields with a fixed gutter; collapsed fields leave no holes.</summary>
public sealed class ProfileParameterPanel : Panel
{
    private const double ColumnGap = 20;
    private const double RowGap = 12;
    private readonly List<double> _rowHeights = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        _rowHeights.Clear();
        var fields = InternalChildren.Cast<UIElement>().Where(child => child.Visibility != Visibility.Collapsed).ToArray();
        var width = double.IsInfinity(availableSize.Width) ? 600 : Math.Max(0, availableSize.Width);
        var columnWidth = Math.Max(0, (width - ColumnGap) / 2);
        for (var index = 0; index < fields.Length; index += 2)
        {
            fields[index].Measure(new Size(columnWidth, double.PositiveInfinity));
            var height = fields[index].DesiredSize.Height;
            if (index + 1 < fields.Length)
            {
                fields[index + 1].Measure(new Size(columnWidth, double.PositiveInfinity));
                height = Math.Max(height, fields[index + 1].DesiredSize.Height);
            }
            _rowHeights.Add(height);
        }
        return new Size(width, _rowHeights.Sum() + Math.Max(0, _rowHeights.Count - 1) * RowGap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columnWidth = Math.Max(0, (finalSize.Width - ColumnGap) / 2);
        var index = 0;
        var top = 0d;
        foreach (UIElement field in InternalChildren)
        {
            if (field.Visibility == Visibility.Collapsed) continue;
            var row = index / 2;
            var height = row < _rowHeights.Count ? _rowHeights[row] : field.DesiredSize.Height;
            field.Arrange(new Rect(index % 2 * (columnWidth + ColumnGap), top, columnWidth, height));
            if (index % 2 == 1) top += height + RowGap;
            index++;
        }
        return finalSize;
    }
}
