using System.Windows;
using System.Windows.Media;
using Launcher.Core.Configuration;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;

namespace Launcher.App;

internal static class AppThemeManager
{
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    public static void Apply(AppTheme theme)
    {
        var resources = Application.Current.Resources;
        var palette = theme switch
        {
            AppTheme.Dark => DarkPalette,
            AppTheme.Light => LightPalette,
            _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "不支持的界面主题。"),
        };

        foreach (var (key, color) in palette.SolidColors)
        {
            resources[key] = CreateBrush(color);
        }

        resources["WindowGradientBrush"] = CreateGradient(palette.WindowGradient);
        resources["AccentGradientBrush"] = CreateGradient(palette.AccentGradient);
        resources["AccentGlowColor"] = ParseColor(palette.AccentGlowColor);
        CurrentTheme = theme;
    }

    private static readonly ThemePalette DarkPalette = new(
        new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#06182B",
            ["PanelBrush"] = "#0A2138",
            ["CardBackgroundBrush"] = "#0B233A",
            ["CardHoverBrush"] = "#102E4B",
            ["PrimaryBrush"] = "#F3F8FF",
            ["SecondaryBrush"] = "#A9C4DE",
            ["MutedBrush"] = "#6F91AF",
            ["AccentBrush"] = "#19B9FF",
            ["AccentBrightBrush"] = "#48E5FF",
            ["DangerBrush"] = "#FF6680",
            ["LineBrush"] = "#24577B",
            ["OnAccentBrush"] = "#FFFFFF",
            ["ToolTipBackgroundBrush"] = "#0A2138",
            ["ControlBorderBrush"] = "#2A83BC",
            ["CardSurfaceBrush"] = "#B20B233A",
            ["CardBorderBrush"] = "#287EB7",
            ["ButtonSurfaceBrush"] = "#132E49",
            ["ButtonHoverBrush"] = "#1B4770",
            ["ButtonPressedBrush"] = "#0E75B8",
            ["DangerTextBrush"] = "#FFE4E9",
            ["DangerSurfaceBrush"] = "#361A29",
            ["InputSurfaceBrush"] = "#0A1D31",
            ["InputBorderBrush"] = "#316587",
            ["ListSurfaceBrush"] = "#07192A",
            ["ComboArrowSurfaceBrush"] = "#133652",
            ["SelectedItemBrush"] = "#15527C",
            ["ScrollTrackBrush"] = "#071A2C",
            ["ScrollThumbBrush"] = "#287EB7",
            ["ScrollThumbBorderBrush"] = "#3BA7E6",
            ["ScrollThumbHoverBrush"] = "#1AAEEB",
            ["ScrollThumbDraggingBrush"] = "#19D4F2",
            ["ProgressBorderBrush"] = "#285A7C",
            ["ToggleTrackBrush"] = "#15324C",
            ["ToggleTrackBorderBrush"] = "#39759E",
            ["ToggleThumbBrush"] = "#B9C9D8",
            ["SelectionBoxBorderBrush"] = "#9BB5CB",
            ["PerformanceRingTrackBrush"] = "#184865",
            ["PerformanceRingProgressBrush"] = "#18D5F8",
            ["WindowBorderBrush"] = "#2786C6",
            ["TitleBarBrush"] = "#B2071A2D",
            ["SidebarBrush"] = "#00000000",
            ["SidebarBorderBrush"] = "#1C557B",
            ["ModePendingCardBrush"] = "#C7133859",
            ["NotificationBackgroundBrush"] = "#9A0A243C",
            ["NotificationBorderBrush"] = "#246C9C",
            ["NavigationSelectedBrush"] = "#BE124069",
            ["TitleButtonHoverBrush"] = "#183A59",
            ["TitleButtonPressedBrush"] = "#24608F",
            ["TitleCloseHoverBrush"] = "#B9344A",
            ["TitleClosePressedBrush"] = "#8F2739",
            ["WarningBackgroundBrush"] = "#FFF7E6",
            ["WarningTextBrush"] = "#7A4B00",
            ["ProfileWarningBrush"] = "#B45309",
        },
        ["#071B31", "#031324", "#08243D"],
        ["#176EFF", "#09C9FF"],
        "#22D3EE");

    private static readonly ThemePalette LightPalette = new(
        new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#F2F7FA",
            ["PanelBrush"] = "#E6F0F7",
            ["CardBackgroundBrush"] = "#FFFFFF",
            ["CardHoverBrush"] = "#EAF4FA",
            ["PrimaryBrush"] = "#102437",
            ["SecondaryBrush"] = "#405B70",
            ["MutedBrush"] = "#71879A",
            ["AccentBrush"] = "#0B91C7",
            ["AccentBrightBrush"] = "#087FAF",
            ["DangerBrush"] = "#C83D5A",
            ["LineBrush"] = "#BCD1DE",
            ["OnAccentBrush"] = "#FFFFFF",
            ["ToolTipBackgroundBrush"] = "#FFFFFF",
            ["ControlBorderBrush"] = "#9CBED2",
            ["CardSurfaceBrush"] = "#F9FCFE",
            ["CardBorderBrush"] = "#AFC9DA",
            ["ButtonSurfaceBrush"] = "#E2EEF6",
            ["ButtonHoverBrush"] = "#D2E7F3",
            ["ButtonPressedBrush"] = "#BBDCEA",
            ["DangerTextBrush"] = "#8D1732",
            ["DangerSurfaceBrush"] = "#FBE8ED",
            ["InputSurfaceBrush"] = "#F8FBFD",
            ["InputBorderBrush"] = "#9DBDCE",
            ["ListSurfaceBrush"] = "#F8FBFD",
            ["ComboArrowSurfaceBrush"] = "#E4EFF6",
            ["SelectedItemBrush"] = "#C7E4F3",
            ["ScrollTrackBrush"] = "#E5EEF4",
            ["ScrollThumbBrush"] = "#68AACB",
            ["ScrollThumbBorderBrush"] = "#4B98BD",
            ["ScrollThumbHoverBrush"] = "#3998C1",
            ["ScrollThumbDraggingBrush"] = "#168BB5",
            ["ProgressBorderBrush"] = "#A9C5D5",
            ["ToggleTrackBrush"] = "#D7E4EC",
            ["ToggleTrackBorderBrush"] = "#9CB8C9",
            ["ToggleThumbBrush"] = "#FFFFFF",
            ["SelectionBoxBorderBrush"] = "#7695AA",
            ["PerformanceRingTrackBrush"] = "#C5D9E6",
            ["PerformanceRingProgressBrush"] = "#0B9BC5",
            ["WindowBorderBrush"] = "#7DB4D0",
            ["TitleBarBrush"] = "#F4F8FA",
            ["SidebarBrush"] = "#00000000",
            ["SidebarBorderBrush"] = "#ABC7D8",
            ["ModePendingCardBrush"] = "#DCEFF8",
            ["NotificationBackgroundBrush"] = "#E6F2FA",
            ["NotificationBorderBrush"] = "#9DBED1",
            ["NavigationSelectedBrush"] = "#DCEBF4",
            ["TitleButtonHoverBrush"] = "#D7E7F0",
            ["TitleButtonPressedBrush"] = "#C1DAE8",
            ["TitleCloseHoverBrush"] = "#E14B61",
            ["TitleClosePressedBrush"] = "#C5344A",
            ["WarningBackgroundBrush"] = "#FFF7E6",
            ["WarningTextBrush"] = "#7A4B00",
            ["ProfileWarningBrush"] = "#9A5800",
        },
        ["#F5F8FA", "#F1F5F8", "#EEF3F6"],
        ["#2477E8", "#00A9D6"],
        "#33B6D6");

    private static SolidColorBrush CreateBrush(string value)
    {
        var brush = new SolidColorBrush(ParseColor(value));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateGradient(IReadOnlyList<string> colors)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        for (var index = 0; index < colors.Count; index++)
        {
            brush.GradientStops.Add(new GradientStop(
                ParseColor(colors[index]),
                colors.Count == 1 ? 0d : (double)index / (colors.Count - 1)));
        }

        brush.Freeze();
        return brush;
    }

    private static Color ParseColor(string value) =>
        (Color)MediaColorConverter.ConvertFromString(value);

    private sealed record ThemePalette(
        IReadOnlyDictionary<string, string> SolidColors,
        IReadOnlyList<string> WindowGradient,
        IReadOnlyList<string> AccentGradient,
        string AccentGlowColor);
}
