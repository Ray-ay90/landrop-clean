using System.Windows;
using System.Windows.Threading;

namespace LanDrop.Windows;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.WriteStartup();

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception.", e.Exception);
        MessageBox.Show(
            "应用遇到错误，已记录日志。\n\n日志目录：data\\logs",
            "局域投送",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled AppDomain exception.", e.ExceptionObject as Exception);
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}
