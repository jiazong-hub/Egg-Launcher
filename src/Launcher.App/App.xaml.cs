using System.Configuration;
using System.Data;
using System.Windows;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;

namespace Launcher.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = @"Local\ChatGPTLocalLauncher.App";
    private const string ActivationEventName = @"Local\ChatGPTLocalLauncher.App.Activate";
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: InstanceMutexName,
            createdNew: out var ownsSingleInstance);
        if (!ownsSingleInstance)
        {
            _activationEvent.Set();
            _activationEvent.Dispose();
            _activationEvent = null;
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) => Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                () => Windows.OfType<MainWindow>().FirstOrDefault()?.RestoreFromExternalActivation()),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
        ApplySavedPreferences();
        base.OnStartup(e);
    }

    private static void ApplySavedPreferences()
    {
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser();
            using var settingsStore = new JsonSettingsStore(paths.SettingsFile);
            var settings = settingsStore.LoadAsync().GetAwaiter().GetResult();
            AppThemeManager.Apply(settings.Theme);
            AppLanguageManager.Apply(settings.Language);
        }
        catch
        {
            AppThemeManager.Apply(AppTheme.Dark);
            AppLanguageManager.Apply(AppLanguage.System);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationWait?.Unregister(null);
        _activationWait = null;
        _activationEvent?.Dispose();
        _activationEvent = null;
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }
}

