using System.Reflection;
using Microsoft.UI.Xaml;
using Velopack;
using Velopack.Sources;
using Windows.System;

namespace Shuo;

public sealed partial class MainWindow
{
    private const string ReleaseRepository = "https://github.com/thomjiji/shuo";
    private UpdateManager? _updateManager;
    private UpdateInfo? _availableUpdate;
    private VelopackAsset? _downloadedUpdate;
    private DispatcherTimer? _updateTimer;
    private bool _checkingUpdate;
    private bool _installingUpdate;
    private int _pendingPastes;
    private string? _acknowledgedUpdateVersion;
    private string? _notifiedVersion;

    private void InitializeUpdates()
    {
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "未知";
        AppVersionLabel.Text = $"当前版本：{version}";
        AppTitleBar.Title = $"说 {version}";
        Title = $"说 {version}";
        try
        {
            _updateManager = new UpdateManager(new GithubSource(ReleaseRepository, null, false, new SystemProxyDownloader()));
            if (!_updateManager.IsInstalled || _updateManager.IsPortable)
            {
                UpdateStatus.Text = "当前为便携版。安装后可在应用内接收更新提示并更新。";
                CheckUpdateButton.Content = "下载安装版";
                return;
            }
            _downloadedUpdate = _updateManager.UpdatePendingRestart;
            if (_downloadedUpdate is not null) ShowAvailableUpdate(_downloadedUpdate.Version.ToString());
            UpdateStatus.Text = _downloadedUpdate is null ? "启动时及每 6 小时检查更新，点击后才会下载并安装。" : "更新已下载，点击更新并重启。";
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            _updateTimer.Tick += (_, _) => _ = CheckUpdatesAsync(false);
            _updateTimer.Start();
            _ = CheckUpdatesAsync(false);
        }
        catch (Exception error) { UpdateStatus.Text = "更新服务暂不可用：" + error.Message; }
    }

    private void ShowAvailableUpdate(string version)
    {
        UpdateStatus.Text = $"发现新版本 {version}。";
        InstallUpdateButton.Visibility = Visibility.Visible;
        if ((SettingsNavigation.SelectedItem as Microsoft.UI.Xaml.Controls.NavigationViewItem)?.Tag as string == "general")
            _acknowledgedUpdateVersion = version;
        UpdateInfoBadge.Visibility = _acknowledgedUpdateVersion == version
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateInstallControls();
        if (_notifiedVersion != version)
        {
            _notifiedVersion = version;
            _tray.NotifyUpdate(version);
        }
    }

    private void AcknowledgeAvailableUpdate()
    {
        var version = _availableUpdate?.TargetFullRelease.Version.ToString()
            ?? _downloadedUpdate?.Version.ToString();
        if (version is null) return;
        _acknowledgedUpdateVersion = version;
        UpdateInfoBadge.Visibility = Visibility.Collapsed;
    }

    private void UpdateInstallControls()
    {
        if (InstallUpdateButton is null || CheckUpdateButton is null) return;
        var idle = _readingCancellation is null && _translationCancellation is null && !_exiting && !_closed && !_dictationActive && !_togglePending && !_modelChanging && _pendingPastes == 0;
        InstallUpdateButton.IsEnabled = idle && !_installingUpdate && !_checkingUpdate;
        CheckUpdateButton.IsEnabled = !_checkingUpdate && !_installingUpdate;
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (_checkingUpdate || _installingUpdate || _exiting || _updateManager is null || !_updateManager.IsInstalled) return;
        _checkingUpdate = true;
        UpdateInstallControls();
        if (manual) UpdateStatus.Text = "正在检查更新...";
        try
        {
            var update = await _updateManager.CheckForUpdatesAsync();
            if (_exiting || _closed) return;
            _availableUpdate = update;
            if (update is not null) ShowAvailableUpdate(update.TargetFullRelease.Version.ToString());
            else if (_downloadedUpdate is null) UpdateStatus.Text = "当前已是最新版本。";
        }
        catch (Exception error)
        {
            if (!_exiting && !_closed) UpdateStatus.Text = "暂时无法检查更新，可稍后重试：" + error.Message;
        }
        finally
        {
            _checkingUpdate = false;
            if (!_closed) UpdateInstallControls();
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs args)
    {
        if (_updateManager is null || !_updateManager.IsInstalled || _updateManager.IsPortable)
        {
            try { await Launcher.LaunchUriAsync(new Uri(ReleaseRepository + "/releases/latest")); }
            catch (Exception error) { UpdateStatus.Text = "无法打开下载页：" + error.Message; }
            return;
        }
        await CheckUpdatesAsync(true);
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs args)
    {
        if (_readingCancellation is not null || _translationCancellation is not null || _updateManager is null || _checkingUpdate || _installingUpdate || _exiting || _closed || _dictationActive || _togglePending || _modelChanging || _pendingPastes > 0) return;
        if (_availableUpdate is null && _downloadedUpdate is null) return;
        var updateToInstall = _availableUpdate;
        _installingUpdate = true;
        UpdateModelControls();
        UpdateProgress.Visibility = Visibility.Visible;
        try
        {
            if (updateToInstall is not null)
            {
                UpdateStatus.Text = "正在下载更新...";
                await _updateManager.DownloadUpdatesAsync(updateToInstall, progress => DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_closed) UpdateProgress.Value = progress;
                }), _shutdown.Token);
                _downloadedUpdate = updateToInstall.TargetFullRelease;
            }
            _shutdown.Token.ThrowIfCancellationRequested();
            UpdateStatus.Text = "正在退出并安装更新...";
            _updateManager.WaitExitThenApplyUpdates(_downloadedUpdate, silent: false, restart: true);
            await ExitAsync();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_closed) UpdateStatus.Text = "更新失败，可重试。当前版本仍可使用：" + error.Message;
        }
        finally
        {
            _installingUpdate = false;
            if (!_closed && !_exiting)
            {
                UpdateProgress.Visibility = Visibility.Collapsed;
                UpdateModelControls();
            }
        }
    }
}
