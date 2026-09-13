using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shuo.Services;

namespace Shuo;

public sealed partial class MainWindow
{
    private bool _dailyLoaded;
    private bool _dictationPreview;

    private void InitializeDailyTasks()
    {
        try
        {
            var options = DailySettings.Load();
            CaptionLanguage.SelectedIndex = options.CaptionLanguage;
        }
        catch (Exception error) { ListenStatus.Text = "无法读取使用偏好：" + error.Message; }
        _dailyLoaded = true;
        SettingsNavigation.SelectedItem = ListenNavigationItem;
        UpdateDailyControls();
    }

    private void ListenChoice_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!_dailyLoaded) return;
        try { DailySettings.Save(new(CaptionLanguage: CaptionLanguage.SelectedIndex)); }
        catch (Exception error) { TranslationStatus.Text = "偏好未保存：" + error.Message; }
        UpdateDailyControls();
    }

    private void UpdateDailyControls()
    {
        if (!_dailyLoaded) return;
        var listening = _dictationActive || _togglePending || _translationCancellation is not null;
        var occupied = listening || _readingCancellation is not null || _pendingPastes > 0 || _installingUpdate;
        CaptionLanguage.IsEnabled = !occupied;
        ListenButton.Content = _dictationActive ? "完成" : "开始";
        ListenButton.IsEnabled = _dictationActive ? !_togglePending : !occupied && !_modelChanging;
        CaptionButton.Content = _translationCancellation is not null ? "停止字幕" : "开启字幕";
        CaptionButton.IsEnabled = _translationCancellation is { IsCancellationRequested: false } || !occupied;
        if (_dictationActive) ListenStatus.Text = "正在识别...";
        else if (_togglePending) ListenStatus.Text = "正在连接...";
    }

    private void ToggleDailyCaption()
    {
        ToggleTranslation(original: CaptionLanguage.SelectedIndex == 0, fromDaily: true);
    }

    private async void Listen_Click(object sender, RoutedEventArgs args)
    {
        ListenStatus.Text = "";
        var wasActive = _dictationActive;
        await ToggleAsync(preview: true);
        if (!wasActive && !_togglePending && !_dictationActive) ListenStatus.Text = "未能开始，请检查声音识别服务设置。";
    }

    private void Caption_Click(object sender, RoutedEventArgs args) => ToggleDailyCaption();

}
