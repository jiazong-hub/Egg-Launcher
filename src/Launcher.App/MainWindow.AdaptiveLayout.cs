using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Launcher.App;

public partial class MainWindow
{
    private AdaptiveLayoutMode? _adaptiveLayoutMode;

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
        if (!IsInitialized || SidebarColumn is null || MainContentGrid is null)
        {
            return;
        }

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var mode = width switch
        {
            >= 1200 => AdaptiveLayoutMode.Standard,
            >= 960 => AdaptiveLayoutMode.Compact,
            _ => AdaptiveLayoutMode.Narrow,
        };
        var modeChanged = _adaptiveLayoutMode != mode;

        if (force || modeChanged)
        {
            ApplySidebarLayout(mode);
            ApplyWindowSpacing(mode);
            _adaptiveLayoutMode = mode;
        }

        var sidebarWidth = SidebarColumn.Width.Value;
        var horizontalMargin = MainContentGrid.Margin.Left + MainContentGrid.Margin.Right;
        var contentWidth = Math.Max(300, width - sidebarWidth - horizontalMargin - 4);
        var singleColumn = contentWidth < 700;
        ApplyHomeCardLayout(singleColumn);
        ApplyModelCardLayout(singleColumn);
    }

    private void ApplySidebarLayout(AdaptiveLayoutMode mode)
    {
        switch (mode)
        {
            case AdaptiveLayoutMode.Standard:
                SidebarColumn.Width = new GridLength(292);
                SidebarLayoutGrid.Margin = new Thickness(18, 14, 18, 16);
                SidebarLogoImage.Width = SidebarLogoImage.Height = 96;
                SidebarBrandText.Visibility = Visibility.Visible;
                SidebarSubtitleText.Visibility = Visibility.Visible;
                SidebarBrandPanel.Margin = new Thickness(0, 0, 0, 18);
                SidebarFooterPanel.Visibility = Visibility.Visible;
                SetSidebarPerformanceRingSize(48, new Thickness(2, 0, 2, 12));
                SetNavigationTextVisibility(Visibility.Visible);
                break;
            case AdaptiveLayoutMode.Compact:
                SidebarColumn.Width = new GridLength(220);
                SidebarLayoutGrid.Margin = new Thickness(12, 10, 12, 12);
                SidebarLogoImage.Width = SidebarLogoImage.Height = 60;
                SidebarBrandText.Visibility = Visibility.Visible;
                SidebarSubtitleText.Visibility = Visibility.Collapsed;
                SidebarBrandPanel.Margin = new Thickness(0, 0, 0, 10);
                SidebarFooterPanel.Visibility = Visibility.Visible;
                SetSidebarPerformanceRingSize(42, new Thickness(0, 0, 0, 12));
                SetNavigationTextVisibility(Visibility.Visible);
                break;
            default:
                SidebarColumn.Width = new GridLength(82);
                SidebarLayoutGrid.Margin = new Thickness(8, 10, 8, 10);
                SidebarLogoImage.Width = SidebarLogoImage.Height = 42;
                SidebarBrandText.Visibility = Visibility.Collapsed;
                SidebarSubtitleText.Visibility = Visibility.Collapsed;
                SidebarBrandPanel.Margin = new Thickness(0, 0, 0, 10);
                SidebarFooterPanel.Visibility = Visibility.Collapsed;
                SetNavigationTextVisibility(Visibility.Collapsed);
                break;
        }
    }

    private void SetSidebarPerformanceRingSize(double size, Thickness gridMargin)
    {
        SidebarPerformanceGrid.Margin = gridMargin;
        foreach (var ring in new[] { SidebarCpuRing, SidebarMemoryRing, SidebarGpuRing, SidebarVramRing })
        {
            ring.Width = size;
            ring.Height = size;
        }
    }

    private void ApplyWindowSpacing(AdaptiveLayoutMode mode)
    {
        var (titleHeight, titleButtonWidth, contentMargin, cardPadding) = mode switch
        {
            AdaptiveLayoutMode.Standard => (36d, 40d, new Thickness(28, 22, 28, 20), new Thickness(22)),
            AdaptiveLayoutMode.Compact => (34d, 38d, new Thickness(18, 16, 18, 14), new Thickness(18)),
            _ => (32d, 36d, new Thickness(12, 12, 12, 10), new Thickness(15)),
        };

        TitleBarRow.Height = new GridLength(titleHeight);
        MainWindowChrome.CaptionHeight = titleHeight;
        MinimizeButtonColumn.Width = new GridLength(titleButtonWidth);
        MaximizeButtonColumn.Width = new GridLength(titleButtonWidth);
        CloseButtonColumn.Width = new GridLength(titleButtonWidth);
        MainContentGrid.Margin = contentMargin;
        Application.Current.Resources["AdaptiveCardPadding"] = cardPadding;
    }

    private void SetNavigationTextVisibility(Visibility visibility)
    {
        HomeNavText.Visibility = visibility;
        ModeNavText.Visibility = visibility;
        ModelsNavText.Visibility = visibility;

        var showText = visibility == Visibility.Visible;
        foreach (var icon in new[] { HomeNavIcon, ModeNavIcon, ModelsNavIcon })
        {
            icon.HorizontalAlignment = showText
                ? System.Windows.HorizontalAlignment.Left
                : System.Windows.HorizontalAlignment.Center;
            icon.Margin = showText ? new Thickness(24, 0, 0, 0) : new Thickness(0);
        }
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

    private enum AdaptiveLayoutMode
    {
        Standard,
        Compact,
        Narrow,
    }
}
