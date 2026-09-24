using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Launcher.App;

internal static class NestedScrollWheelRouter
{
    public static void Route(DependencyObject? source, ScrollViewer outer, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || source is null)
        {
            return;
        }

        var inner = source as ScrollViewer ?? FindVisualChild<ScrollViewer>(source);
        if (inner is not null && (e.Delta < 0
                ? inner.VerticalOffset < inner.ScrollableHeight
                : inner.VerticalOffset > 0))
        {
            return;
        }

        var targetOffset = Math.Clamp(outer.VerticalOffset - e.Delta, 0, outer.ScrollableHeight);
        if (targetOffset == outer.VerticalOffset)
        {
            return;
        }

        e.Handled = true;
        outer.ScrollToVerticalOffset(targetOffset);
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
