using Velopack;

namespace Shuo;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Installer/update hooks must exit before initializing WinUI or the worker.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(queue));
            new App();
        });
    }
}
