using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Shuo.Services;

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
        var startupLaunch = StartupRegistration.IsStartupLaunch(Environment.GetCommandLineArgs());
#if DEBUG
        // Exercise the real passive overlay without opening an audio session.
        if (Environment.GetCommandLineArgs().Contains("--indicator-preview"))
        {
            _captionPreview = new OverlayWindow();
            _captionPreview.Begin(false);
            var text = "前面的内容已经超过浮窗宽度，理解的一个模型来才能完成这件事情。";
            _captionPreview.UpdateTranscript(text);
            await Task.Delay(700);
            foreach (var glyph in "我猜这样就不会从右边切着出来了。")
            {
                text += glyph;
                _captionPreview.UpdateTranscript(text);
                await Task.Delay(450);
            }
            return;
        }
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
            if (!startupLaunch)
                await _instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Exit();
            return;
        }

        _instance.Activated += (_, _) => _dispatcher.TryEnqueue(() => _window?.ShowSettings());
        _window = new MainWindow();
        if (!startupLaunch) _window.ShowSettings();
        _ = _window.StartAsync();
    }
}
