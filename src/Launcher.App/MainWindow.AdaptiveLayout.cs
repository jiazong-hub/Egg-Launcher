using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media.Animation;

namespace Launcher.App;

public partial class MainWindow
{
    private bool? _homeSingleColumn;
    private bool? _modelSingleColumn;
    private bool? _compactTelemetry;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        AdaptiveWindowSizing.DisableRoundedCorners(this);
        AdaptiveWindowSizing.AttachMaximizeWorkArea(this);
        AdaptiveWindowSizing.FitMainWindow(this);
        ApplyAdaptiveLayout(force: true);
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                AdaptiveWindowSizing.ClampToCurrentWorkArea(this);
                ApplyAdaptiveLayout(force: true);
            });
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyAdaptiveLayout(force: false);

    private void Window_AdaptiveLocationChanged(object? sender, EventArgs e)
    {
        if (IsInitialized) AdaptiveWindowSizing.UpdateMainWindowMinimums(this);
    }

    private void LauncherTabs_AdaptiveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Ignore selection changes bubbling from lists inside the current page.
        if (ReferenceEquals(e.Source, LauncherTabs)) ApplyAdaptiveLayout(force: true);
    }

    private void Window_DpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                AdaptiveWindowSizing.ClampToCurrentWorkArea(this);
                ApplyAdaptiveLayout(force: true);
            });
    }

    private void ApplyAdaptiveLayout(bool force)
    {
        if (!IsInitialized || SidebarColumn is null || MainContentGrid is null) return;

        AdaptiveWindowSizing.UpdateMainWindowMinimums(this);

        var width = ActualWidth > 0 ? ActualWidth : Width;
        // A single continuous curve is shared by every page. It does not depend on
        // card count, scrollbars, or which tab is selected.
        var sidebarWidth = width <= 1120
            ? Lerp(82, 220, (width - 960) / 160)
            : Lerp(220, 292, (width - 1120) / 240);
        SidebarColumn.Width = new GridLength(sidebarWidth);
        ApplySidebarLayout(sidebarWidth);
        ApplyWindowSpacing(width);

        var overhead = 4 + 12 + SystemParameters.VerticalScrollBarWidth
            + LauncherTabs.Padding.Left + LauncherTabs.Padding.Right
            + LauncherTabs.BorderThickness.Left + LauncherTabs.BorderThickness.Right;
        var contentWidth = Math.Max(0, width - sidebarWidth
            - MainContentGrid.Margin.Left - MainContentGrid.Margin.Right - overhead);
        // Preserve the original two-column cards at all normal supported widths.
        // A single column is only a fallback for a work area below the design minimum.
        var homeSingle = contentWidth < 700 + (_homeSingleColumn == true ? 24 : 0);
        var modelSingle = contentWidth < 700 + (_modelSingleColumn == true ? 24 : 0);
        if (force || _homeSingleColumn != homeSingle) ApplyHomeCardLayout(homeSingle);
        if (force || _modelSingleColumn != modelSingle) ApplyModelCardLayout(modelSingle);
        _homeSingleColumn = homeSingle;
        _modelSingleColumn = modelSingle;
    }

    private static double Lerp(double start, double end, double amount)
        => start + (end - start) * Math.Clamp(amount, 0, 1);

    private static void SetProgressiveVisibility(UIElement element, double opacity)
    {
        element.Visibility = opacity <= 0 ? Visibility.Collapsed : Visibility.Visible;
        element.Opacity = Math.Clamp(opacity, 0, 1);
    }

    private void ApplySidebarLayout(double width)
    {
        var progress = Math.Clamp((width - 82) / 210, 0, 1);
        var inset = Lerp(8, 18, (width - 180) / 112);
        SidebarScrollViewer.Margin = new Thickness(inset, 12, inset, 14);
        SidebarLogoImage.Width = SidebarLogoImage.Height = Lerp(42, 96, progress);
        SidebarBrandText.FontSize = Lerp(18, 24, (width - 196) / 96);
        var brandOpacity = Math.Clamp((width - 184) / 12, 0, 1);
        var signatureOpacity = Math.Clamp((width - 208) / 12, 0, 1);
        SetProgressiveVisibility(SidebarBrandText, brandOpacity);
        SidebarBrandText.Height = 32 * brandOpacity;
        SidebarBrandText.Margin = new Thickness(0, 6 * brandOpacity, 0, 0);
        SetProgressiveVisibility(SidebarSignaturePanel, signatureOpacity);
        SidebarSignaturePanel.MaxHeight = 64 * signatureOpacity;
        SidebarSignaturePanel.ClipToBounds = true;
        SidebarBrandPanel.Margin = new Thickness(0, 0, 0, Lerp(10, 18, progress));

        var labelOpacity = Math.Clamp((width - 144) / 36, 0, 1);
        foreach (var label in new[] { HomeNavText, ModeNavText, ModelsNavText })
            SetProgressiveVisibility(label, labelOpacity);
        foreach (var icon in new[] { HomeNavIcon, ModeNavIcon, ModelsNavIcon })
        {
            icon.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            var centeredLeft = Math.Max(0, (width - inset * 2 - icon.Width) / 2);
            icon.Margin = new Thickness(Lerp(centeredLeft, 24, labelOpacity), 0, 0, 0);
        }

        // Two columns keep all four rings readable while the pane narrows.
        SidebarPerformanceGrid.Columns = width < 220 ? 2 : 4;
        SidebarPerformanceGrid.Margin = new Thickness(0, 0, 0, 12);
        var ringSize = Lerp(40, 48, progress);
        foreach (var ring in new[] { SidebarCpuRing, SidebarMemoryRing, SidebarGpuRing, SidebarVramRing })
            ring.Width = ring.Height = ringSize;
        var compactTelemetry = _compactTelemetry == true ? width < 192 : width < 180;
        SidebarFooterPanel.Visibility = compactTelemetry ? Visibility.Collapsed : Visibility.Visible;
        CompactTelemetryBar.Visibility = compactTelemetry ? Visibility.Visible : Visibility.Collapsed;
        if (_compactTelemetry != compactTelemetry)
        {
            CompactTelemetryBar.BeginAnimation(UIElement.OpacityProperty, null);
            CompactTelemetryBar.Opacity = 1;
            if (compactTelemetry && UiMotion.AnimationsEnabled)
                CompactTelemetryBar.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
        }
        _compactTelemetry = compactTelemetry;
    }

    private void ApplyWindowSpacing(double width)
    {
        var progress = Math.Clamp((width - 960) / 400, 0, 1);
        var margin = Lerp(12, 28, progress);
        // Keep the title bar and hit targets stable during a drag.
        TitleBarRow.Height = new GridLength(36);
        MainWindowChrome.CaptionHeight = 36;
        MainContentGrid.Margin = new Thickness(margin, Lerp(12, 22, progress), margin, Lerp(10, 20, progress));
        Application.Current.Resources["AdaptiveCardPadding"] = new Thickness(Lerp(18, 22, progress));
    }

    private void ApplyHomeCardLayout(bool singleColumn)
    {
        SetGridColumns(HomeCardsGrid, singleColumn);
        if (singleColumn)
        {
            PlaceSingleColumnCards([LaunchCard, HardwareCard, CurrentStateCard, SettingsCard]);
            return;
        }

        PlaceTwoColumnCard(LaunchCard, 0, 0);
        PlaceTwoColumnCard(HardwareCard, 0, 1);
        PlaceTwoColumnCard(CurrentStateCard, 1, 0);
        PlaceTwoColumnCard(SettingsCard, 1, 1);
    }

    private void ApplyModelCardLayout(bool singleColumn)
    {
        SetGridColumns(ModelCardsGrid, singleColumn);
        if (singleColumn)
        {
            PlaceSingleColumnCards([RuntimeCard, ManageModelsCard, SearchModelsCard, DownloadsCard]);
            return;
        }

        PlaceTwoColumnCard(RuntimeCard, 0, 0);
        PlaceTwoColumnCard(ManageModelsCard, 0, 1);
        PlaceTwoColumnCard(SearchModelsCard, 1, 0);
        PlaceTwoColumnCard(DownloadsCard, 1, 1);
    }

    private static void SetGridColumns(Grid grid, bool singleColumn)
    {
        // A small-screen fallback must not turn cards into full-window banners.
        grid.MaxWidth = singleColumn ? 680 : double.PositiveInfinity;
        grid.HorizontalAlignment = singleColumn
            ? System.Windows.HorizontalAlignment.Left
            : System.Windows.HorizontalAlignment.Stretch;
        grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = singleColumn
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
    }

    private static void PlaceSingleColumnCards(IReadOnlyList<FrameworkElement> cards)
    {
        for (var index = 0; index < cards.Count; index++)
        {
            Grid.SetRow(cards[index], index);
            Grid.SetColumn(cards[index], 0);
            cards[index].Margin = new Thickness(0, 0, 0, index == cards.Count - 1 ? 0 : 12);
        }
    }

    private static void PlaceTwoColumnCard(FrameworkElement card, int row, int column)
    {
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        card.Margin = new Thickness(
            column == 0 ? 0 : 8,
            row == 0 ? 0 : 8,
            column == 0 ? 8 : 0,
            row == 0 ? 8 : 0);
    }

}
