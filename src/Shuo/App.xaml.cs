using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Shuo;

public partial class App : Application
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private MainWindow? _window;
    private AppInstance? _instance;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _instance = AppInstance.FindOrRegisterForKey("Shuo.Main");
        if (!_instance.IsCurrent)
        {
            await _instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Exit();
            return;
        }

        _instance.Activated += (_, _) => _dispatcher.TryEnqueue(() => _window?.ShowSettings());
        _window = new MainWindow();
        _window.ShowSettings();
        _ = _window.StartAsync();
    }
}
