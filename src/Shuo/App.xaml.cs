using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Shuo;

public partial class App : Application
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private MainWindow? _window;
    private AppInstance? _instance;
#if DEBUG
    private OverlayWindow? _captionPreview;
#endif

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
#if DEBUG
        // Exercise the real passive overlay without opening an audio session.
        if (Environment.GetCommandLineArgs().Contains("--caption-preview"))
        {
            _captionPreview = new OverlayWindow();
            _captionPreview.Begin(false, translation: true);
            for (var i = 1; i <= 8; i++) _captionPreview.AddCaption($"字幕窗口测试 {i}：正文可以拖动，四个角和四条边可以调整大小。");
            _captionPreview.FinishTranslation();
            return;
        }
#endif
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
