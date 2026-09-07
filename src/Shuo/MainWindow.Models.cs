using System.Net.Http;
using Microsoft.UI.Xaml;
using Shuo.Services;

namespace Shuo;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _modelDownload;
    private string? _downloadedModelToSelect;
    private static string DownloadedModelPath => Path.Combine(Path.GetDirectoryName(HotkeySettings.GetPath())!, "models", LocalModelDownload.FileName);

    private void UpdateModelDownloadControls()
    {
        var available = ModelPicker.Items.OfType<LocalModel>().FirstOrDefault(model =>
            string.Equals(Path.GetFileName(model.Path), LocalModelDownload.FileName, StringComparison.OrdinalIgnoreCase));
        var downloading = _modelDownload is not null;
        DownloadModelButton.Content = downloading ? "取消" : "下载";
        DownloadModelButton.Visibility = downloading || (!_loadingModels && available is null) ? Visibility.Visible : Visibility.Collapsed;
        DownloadModelButton.IsEnabled = downloading || (!_loadingModels && !_installingUpdate && !_exiting && _daemonReady);
    }

    private async void DownloadModelButton_Click(object sender, RoutedEventArgs args)
    {
        if (_modelDownload is not null)
        {
            _modelDownload.Cancel();
            return;
        }
        if (_installingUpdate || _exiting || !_daemonReady) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        cancellation.CancelAfter(TimeSpan.FromMinutes(30));
        _modelDownload = cancellation;
        ModelDownloadProgress.Value = 0;
        ModelDownloadProgress.Visibility = Visibility.Visible;
        ModelDownloadStatus.Visibility = Visibility.Collapsed;
        ModelDownloadStatus.Text = "正在连接模型下载源...";
        UpdateModelDownloadControls();
        try
        {
            using var client = new HttpClient(new WinHttpHandler
            {
                WindowsProxyUsePolicy = WindowsProxyUsePolicy.UseWinInetProxy,
                AutomaticRedirection = true,
                MaxAutomaticRedirections = 10,
            }) { Timeout = TimeSpan.FromSeconds(60) };
            var progress = new Progress<double>(value =>
            {
                if (_closed || _exiting || _modelDownload != cancellation) return;
                ModelDownloadProgress.Value = value;
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(ModelDownloadProgress, $"{value:0}%");
            });
            await LocalModelDownload.DownloadAsync(client, DownloadedModelPath, progress, cancellation.Token);
            if (_closed || _exiting) return;
            ModelDownloadStatus.Text = "下载完成，文件校验通过。";
            if (!_cloudOptions.Enabled && !_dictationActive && !_togglePending && !_modelChanging)
                _downloadedModelToSelect = DownloadedModelPath;
            _loadingModels = true;
            await _daemon.SendAsync("models");
        }
        catch (OperationCanceledException)
        {
            if (!_closed && !_exiting && !_shutdown.IsCancellationRequested)
            {
                ModelDownloadStatus.Visibility = Visibility.Visible;
                ModelDownloadStatus.Text = "下载已取消或超时，可重新下载。";
            }
        }
        catch (Exception error)
        {
            _loadingModels = false;
            _downloadedModelToSelect = null;
            if (!_closed && !_exiting)
            {
                ModelDownloadStatus.Visibility = Visibility.Visible;
                ModelDownloadStatus.Text = "下载失败，可重试：" + error.Message;
            }
        }
        finally
        {
            _modelDownload = null;
            if (!_closed && !_exiting)
            {
                ModelDownloadProgress.Visibility = Visibility.Collapsed;
                UpdateModelControls();
            }
        }
    }
}
