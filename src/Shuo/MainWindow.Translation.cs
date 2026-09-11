using Microsoft.UI.Xaml;
using Shuo.Services;

namespace Shuo;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _translationCancellation;
    private Task? _translationTask;

    private bool CanStartTranslation => _readingCancellation is null && !_exiting && !_closed && !_installingUpdate
        && !_dictationActive && !_togglePending && !_modelChanging && _pendingPastes == 0;

    private void InitializeTranslation()
    {
        _overlay.TranslationCloseRequested += StopTranslation;
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(TranslationModelPicker, TranslationSession.Model);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TranslationModelPicker, "翻译模型：" + TranslationSession.Model);
        try
        {
            var options = TranslationSettings.Load();
            TranslationWorkspace.Text = options.WorkspaceId;
            TranslationApiKey.Password = TranslationSettings.LoadApiKey();
            TranslationLanguage.SelectedIndex = options.TargetLanguage == "en" ? 1 : 0;
        }
        catch (Exception error) { TranslationStatus.Text = "无法读取翻译设置：" + error.Message; }
    }

    private void StopTranslation()
    {
        if (_translationCancellation is not { } active) return;
        TranslationStatus.Text = "正在停止采集并等待最后一段译文...";
        active.Cancel();
    }

    private void UpdateTranslationControls()
    {
        if (TranslationButton is null) return;
        var running = _translationCancellation is not null;
        TranslationButton.Content = running ? "停止翻译" : "开始翻译";
        TranslationButton.IsEnabled = running || CanStartTranslation;
        TranslationWorkspace.IsEnabled = TranslationApiKey.IsEnabled = TranslationLanguage.IsEnabled = !running;
    }

    private async void TranslationButton_Click(object sender, RoutedEventArgs args)
    {
        if (_translationCancellation is not null) { StopTranslation(); return; }
        if (!CanStartTranslation) return;
        var options = new TranslationOptions(WorkspaceId: TranslationWorkspace.Text.Trim(),
            TargetLanguage: TranslationLanguage.SelectedIndex == 1 ? "en" : "zh");
        string apiKey;
        try
        {
            TranslationSession.Endpoint(options);
            apiKey = TranslationApiKey.Password.Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("请填写翻译 API Key。");
            TranslationSettings.Save(options, TranslationApiKey.Password);
        }
        catch (Exception error) { TranslationStatus.Text = error.Message; return; }
        var cancellation = _translationCancellation = new CancellationTokenSource();
        TranslationStatus.Text = "正在连接百炼实时翻译...";
        _overlay.Begin(true, translation: true);
        UpdateModelControls();
        _translationTask = RunTranslationAsync(options, apiKey, cancellation);
        await _translationTask;
    }

    private async Task RunTranslationAsync(TranslationOptions options, string apiKey, CancellationTokenSource cancellation)
    {
        void Dispatch(Action action) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_exiting && !_closed
                && ReferenceEquals(_translationCancellation, cancellation)) action();
        });
        try
        {
            await Task.Run(() => new TranslationSession(options, apiKey,
                () => Dispatch(() => { if (cancellation.IsCancellationRequested) return; _overlay.Recording(); TranslationStatus.Text = "正在翻译系统声音。按听写快捷键也可停止。"; }),
                text => Dispatch(() => _overlay.UpdateTranscript(text)),
                level => Dispatch(() => _overlay.UpdateAudioLevel(level)),
                (text, token) => TranscriptFormatter.FormatAsync(text, _autocorrectPath, token)).RunAsync(cancellation.Token));
            if (!_closed && !_exiting) TranslationStatus.Text = "翻译已停止。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_closed && !_exiting) TranslationStatus.Text = "翻译已停止。";
        }
        catch (Exception error)
        {
            if (!_closed && !_exiting) TranslationStatus.Text = "翻译失败：" + error.Message;
        }
        finally
        {
            _translationCancellation = null;
            cancellation.Dispose();
            if (!_closed && !_exiting) { _overlay.Hide(); UpdateModelControls(); }
        }
    }
}
