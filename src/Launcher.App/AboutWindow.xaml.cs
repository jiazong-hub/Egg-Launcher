using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

namespace Launcher.App;

public partial class AboutWindow : Window
{
    private const string TermsChinesePath = @"Legal\TERMS.zh-CN.txt";
    private const string TermsEnglishPath = @"Legal\TERMS.en-US.txt";
    private const string PrivacyChinesePath = @"Legal\PRIVACY.zh-CN.txt";
    private const string PrivacyEnglishPath = @"Legal\PRIVACY.en-US.txt";
    private const string LicensePath = "LICENSE";
    private const string ThirdPartyPath = "THIRD-PARTY-NOTICES.md";

    private readonly string _versionSummary;

    public AboutWindow(string displayVersion)
    {
        InitializeComponent();
        UiMotion.AttachWindowEntrance(this);
        SourceInitialized += (_, _) =>
            AdaptiveWindowSizing.FitDialog(this, 820, 700, 620, 500);

        var assembly = typeof(AboutWindow).Assembly;
        var copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
            ?? "Copyright © 2026 甲总不是贾总";
        VersionText.Text = AppLanguageManager.Choose(
            $"版本 {displayVersion} · 公测版",
            $"Version {displayVersion} · Public Beta");
        CopyrightText.Text = copyright;
        _versionSummary = AppLanguageManager.IsEnglish
            ? $"Egg Launcher {displayVersion} (Public Beta){Environment.NewLine}" +
              $"{copyright}{Environment.NewLine}" +
              $"System: {RuntimeInformation.OSDescription}{Environment.NewLine}" +
              $"Runtime: {RuntimeInformation.FrameworkDescription}{Environment.NewLine}" +
              "License: MIT License"
            : $"Egg Launcher {displayVersion}（公测版）{Environment.NewLine}" +
              $"{copyright}{Environment.NewLine}" +
              $"系统：{RuntimeInformation.OSDescription}{Environment.NewLine}" +
              $"运行时：{RuntimeInformation.FrameworkDescription}{Environment.NewLine}" +
              "许可：MIT License";
    }

    private async void AboutTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded
            || !ReferenceEquals(e.Source, AboutTabs)
            || AboutTabs.SelectedItem is not System.Windows.Controls.TabItem { Content: FrameworkElement content })
        {
            return;
        }

        await UiMotion.AnimateEntranceAsync(
            content,
            offsetY: 5,
            durationMilliseconds: UiMotion.StandardMilliseconds);
    }

    private void OpenTermsButton_Click(object sender, RoutedEventArgs e) =>
        ShowLegalDocument(
            AppLanguageManager.Choose("使用条款与免责声明", "Terms of Use and Disclaimer"),
            AppLanguageManager.IsEnglish ? TermsEnglishPath : TermsChinesePath);

    private void OpenPrivacyButton_Click(object sender, RoutedEventArgs e) =>
        ShowLegalDocument(
            AppLanguageManager.Choose("隐私说明", "Privacy Notice"),
            AppLanguageManager.IsEnglish ? PrivacyEnglishPath : PrivacyChinesePath);

    private void OpenLicenseButton_Click(object sender, RoutedEventArgs e) =>
        ShowLegalDocument("MIT License", LicensePath);

    private void OpenThirdPartyButton_Click(object sender, RoutedEventArgs e) =>
        ShowLegalDocument(AppLanguageManager.Choose("第三方许可", "Third-Party Licenses"), ThirdPartyPath);

    private void CopyVersionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(_versionSummary);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                AppLanguageManager.Choose(
                    $"无法复制版本信息：{exception.Message}",
                    $"Unable to copy version information: {exception.Message}"),
                AppLanguageManager.Text("AboutTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ShowLegalDocument(string title, string relativePath)
    {
        try
        {
            var dialog = new LegalDocumentWindow(title, ReadBundledText(relativePath))
            {
                Owner = this,
            };
            dialog.ShowDialog();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                AppLanguageManager.Choose(
                    $"无法读取随软件提供的文档：{exception.Message}",
                    $"Unable to read the bundled document: {exception.Message}"),
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static string ReadBundledText(string relativePath)
    {
        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
        var expectedPrefix = baseDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? baseDirectory
            : baseDirectory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(AppLanguageManager.Choose(
                "文档路径不在应用目录中。",
                "The document path is outside the application directory."));
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(AppLanguageManager.Choose(
                "发布包缺少所需文档。",
                "The release package is missing the required document."), fullPath);
        }

        return File.ReadAllText(fullPath);
    }
}
