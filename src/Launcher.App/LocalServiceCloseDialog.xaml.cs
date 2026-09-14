using System.Windows;

namespace Launcher.App;

public partial class LocalServiceCloseDialog : Window
{
    public LocalServiceCloseDialog()
    {
        InitializeComponent();
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
