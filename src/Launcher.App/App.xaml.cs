using System.Configuration;
using System.Data;
using System.Windows;

namespace Launcher.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\ChatGPTLocalLauncher.App",
            createdNew: out var ownsSingleInstance);
        if (!ownsSingleInstance)
        {
            MessageBox.Show(
                "ChatGPT Local Launcher 已经在当前用户会话中运行。",
                "ChatGPT Local Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }
}

