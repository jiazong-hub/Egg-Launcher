using System.Windows;

namespace Launcher.App;

public partial class LocalServiceCloseDialog : Window
{
    public LocalServiceCloseDialog()
    {
        InitializeComponent();
        UiMotion.AttachWindowEntrance(this);
        SourceInitialized += (_, _) =>
            AdaptiveWindowSizing.FitDialog(this, 560, 330, 460, 280);
    }

    public LocalServiceCloseChoice Choice { get; private set; } = LocalServiceCloseChoice.Cancel;

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = LocalServiceCloseChoice.Cancel;
        DialogResult = false;
    }

    private void KeepServiceButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = LocalServiceCloseChoice.KeepRunning;
        DialogResult = true;
    }

    private void StopServiceButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = LocalServiceCloseChoice.StopAndExit;
        DialogResult = true;
    }
}

public enum LocalServiceCloseChoice
{
    Cancel,
    KeepRunning,
    StopAndExit,
}
