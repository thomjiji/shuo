using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shuo.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Shuo;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _textTranslationCancellation;
    private bool _dailyLoaded;
    private int _dictationDestination;
    private bool _textResultComplete;

    private void InitializeDailyTasks()
    {
        try
        {
            var options = DailySettings.Load();
            ListenSource.SelectedIndex = options.Source;
            ListenDestination.SelectedIndex = options.Destination;
            ListenLanguage.SelectedIndex = options.CaptionLanguage;
            TextLanguage.SelectedIndex = options.TextLanguage;
        }
        catch (Exception error) { ListenStatus.Text = "无法读取使用偏好：" + error.Message; }
        _dailyLoaded = true;
        SettingsNavigation.SelectedItem = ListenNavigationItem;
        UpdateDailyControls();
    }

    private void ListenChoice_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!_dailyLoaded) return;
        if (ListenSource.SelectedIndex == 1) ListenDestination.SelectedIndex = 2;
        try { DailySettings.Save(new(ListenSource.SelectedIndex, ListenDestination.SelectedIndex, ListenLanguage.SelectedIndex, TextLanguage.SelectedIndex)); }
        catch (Exception error) { ListenStatus.Text = "偏好未保存：" + error.Message; }
        UpdateDailyControls();
    }

    private void UpdateDailyControls()
    {
        if (!_dailyLoaded) return;
        var listening = _dictationActive || _togglePending || _translationCancellation is not null;
        var occupied = listening || _readingCancellation is not null || _textTranslationCancellation is not null || _pendingPastes > 0 || _installingUpdate;
        ListenSource.IsEnabled = !occupied;
        ListenDestination.IsEnabled = !occupied && ListenSource.SelectedIndex == 0;
        ListenLanguage.Visibility = ListenDestination.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        ListenLanguage.IsEnabled = !occupied;
        ListenCleanupOptions.Visibility = ListenDestination.SelectedIndex == 2 ? Visibility.Collapsed : Visibility.Visible;
        ListenButton.Content = listening ? "停止听" : "开始听";
        ListenButton.IsEnabled = listening ? !_togglePending : !occupied && !_modelChanging;
        ListenHint.Text = ListenDestination.SelectedIndex == 2 ? $"字幕显示在浮窗中，可以暂停或停止。快捷键：{_translationHotkeyBinding.DisplayText}。"
            : ListenDestination.SelectedIndex == 1 ? $"识别完成后复制文字。快捷键：{_hotkeyBinding?.DisplayText ?? "未设置"}。"
            : $"在任意输入框按 {_hotkeyBinding?.DisplayText ?? "未设置"} 开始，再按一次完成输入。这里的按钮可在下方试用。";
        SpeakHint.Text = $"也可以在其他应用选中文字，按 {_readingHotkeyBinding.DisplayText} 直接朗读。";
        TextTranslateButton.IsEnabled = !occupied;
        TextStopButton.IsEnabled = _textTranslationCancellation is not null;
        TextSource.IsReadOnly = _textTranslationCancellation is not null;
        TextLanguage.IsEnabled = _textTranslationCancellation is null;
        TextCopyButton.IsEnabled = _textResultComplete;
        TextSpeakButton.IsEnabled = _textResultComplete && !occupied;
        if (_translationCancellation is not null) ListenStatus.Text = TranslationStatus.Text;
        else if (_dictationActive) ListenStatus.Text = "正在听，再按一次完成。";
        else if (_togglePending) ListenStatus.Text = "正在连接...";
    }

    private void ToggleDailyCaption()
    {
        if (ListenDestination.SelectedIndex != 2) { ToggleTranslation(); return; }
        TranslationLanguage.SelectedIndex = ListenLanguage.SelectedIndex == 2 ? 1 : 0;
        ToggleTranslation(ListenLanguage.SelectedIndex == 0, ListenSource.SelectedIndex == 0);
    }

    private async void Listen_Click(object sender, RoutedEventArgs args)
    {
        if (_translationCancellation is not null) { StopTranslation(); return; }
        if (_dictationActive)
        {
            if (_dictationDestination == 0) ListenResult.Focus(FocusState.Programmatic);
            await ToggleAsync();
            return;
        }
        ListenStatus.Text = "";
        if (ListenDestination.SelectedIndex == 2)
        {
            TranslationLanguage.SelectedIndex = ListenLanguage.SelectedIndex == 2 ? 1 : 0;
            ToggleTranslation(ListenLanguage.SelectedIndex == 0, ListenSource.SelectedIndex == 0, fromDaily: true);
            ListenStatus.Text = TranslationStatus.Text;
        }
        else
        {
            if (ListenDestination.SelectedIndex == 0) ListenResult.Focus(FocusState.Programmatic);
            await ToggleAsync();
            if (!_togglePending && !_dictationActive) ListenStatus.Text = "未能开始，请检查声音识别服务设置。";
        }
    }

    private static void CopyDailyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private async void TextTranslate_Click(object sender, RoutedEventArgs args)
    {
        if (_textTranslationCancellation is not null || _readingCancellation is not null || _translationCancellation is not null
            || _dictationActive || _togglePending || _pendingPastes > 0 || _installingUpdate) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _textTranslationCancellation = cancellation;
        _textResultComplete = false;
        TextResult.Text = "";
        TextStatus.Text = "正在翻译...";
        UpdateModelControls();
        try
        {
            var translator = new SelfHostedTextTranslator(SelfHostedTextTranslator.Endpoint(ReadingSettings.Load().SelfHostedHost));
            var target = TextLanguage.SelectedIndex == 1 ? "en" : "zh";
            var parts = ReadingText.Split(TextSource.Text, ReadingText.LocalRequestBytes);
            foreach (var part in parts)
            {
                var translated = await translator.TranslateAsync(part, target, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (TextResult.Text.Length > 0) TextResult.Text += Environment.NewLine + Environment.NewLine;
                TextResult.Text += translated;
            }
            _textResultComplete = true;
            TextStatus.Text = "已完成";
        }
        catch (OperationCanceledException) { if (!_closed) TextStatus.Text = "已停止，译文未完成。"; }
        catch (Exception error) { if (!_closed) TextStatus.Text = "翻译未完成：" + error.Message; }
        finally
        {
            _textTranslationCancellation = null;
            if (!_closed) UpdateModelControls();
        }
    }

    private void TextStop_Click(object sender, RoutedEventArgs args) => _textTranslationCancellation?.Cancel();
    private void TextCopy_Click(object sender, RoutedEventArgs args)
    {
        try { CopyDailyText(TextResult.Text); TextStatus.Text = "已复制"; }
        catch (Exception error) { TextStatus.Text = "复制失败：" + error.Message; }
    }
    private void TextSpeak_Click(object sender, RoutedEventArgs args)
    {
        ReadingTextBox.Text = TextResult.Text;
        ReadingMode.SelectedIndex = 0;
        SettingsNavigation.SelectedItem = SpeakNavigationItem;
        StartReading("text");
    }
}
