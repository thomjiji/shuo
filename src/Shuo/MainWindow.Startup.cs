using Microsoft.UI.Xaml;
using Shuo.Services;

namespace Shuo;

public sealed partial class MainWindow
{
    private readonly StartupRegistration _startupRegistration = StartupRegistration.CreateCurrent();
    private bool _updatingStartupControl = true;

    private void InitializeStartupRegistration()
    {
        try
        {
            ApplyStartupSnapshot(_startupRegistration.Refresh());
        }
        catch (Exception error)
        {
            ShowError("无法读取自动启动设置", error.Message);
        }
        finally
        {
            _updatingStartupControl = false;
        }
    }

    private void RefreshStartupRegistration()
    {
        if (StartupAtLogin is null) return;
        _updatingStartupControl = true;
        try
        {
            ApplyStartupSnapshot(_startupRegistration.Refresh());
        }
        catch (Exception error)
        {
            ShowError("无法读取自动启动设置", error.Message);
        }
        finally
        {
            _updatingStartupControl = false;
        }
    }

    private void ApplyStartupSnapshot(StartupRegistrationSnapshot snapshot)
    {
        StartupAtLogin.IsOn = snapshot.State == StartupRegistrationState.Enabled;
    }

    private void StartupAtLogin_Toggled(object sender, RoutedEventArgs args)
    {
        if (_updatingStartupControl) return;
        var enabled = StartupAtLogin.IsOn;
        try
        {
            ApplyStartupSnapshot(_startupRegistration.SetEnabled(enabled));
        }
        catch (Exception error)
        {
            _updatingStartupControl = true;
            try { ApplyStartupSnapshot(_startupRegistration.Read()); }
            catch { StartupAtLogin.IsOn = !enabled; }
            finally { _updatingStartupControl = false; }
            ShowError("无法更改自动启动设置", error.Message);
        }
    }
}
