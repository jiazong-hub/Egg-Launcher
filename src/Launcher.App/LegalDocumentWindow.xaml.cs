using System.Windows;

namespace Launcher.App;

public partial class LegalDocumentWindow : Window
{
    public LegalDocumentWindow(string title, string content)
    {
        InitializeComponent();
        UiMotion.AttachWindowEntrance(this);
        Title = title;
        HeadingText.Text = title;
        DocumentTextBox.Text = content;
        SourceInitialized += (_, _) =>
            AdaptiveWindowSizing.FitDialog(this, 760, 680, 540, 420);
    }

    private void CopyDocumentButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(DocumentTextBox.Text);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                AppLanguageManager.Choose(
                    $"无法复制文档：{exception.Message}",
                    $"Unable to copy the document: {exception.Message}"),
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
