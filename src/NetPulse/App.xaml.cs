using System.IO;
using System.Windows;

namespace NetPulse;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("操作未完成：" + args.Exception.GetBaseException().Message, "NetPulse", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var smokeIndex = Array.IndexOf(e.Args, "--smoke-test");
        var output = smokeIndex >= 0 && smokeIndex + 1 < e.Args.Length ? e.Args[smokeIndex + 1] : Path.Combine(Path.GetTempPath(), "NetPulse-smoke");
        var window = new MainWindow(smokeIndex >= 0 ? Path.Combine(output, "appdata") : null, automaticStartup: smokeIndex < 0);
        MainWindow = window;
        window.Show();
        if (e.Args.Contains("--smoke-test"))
        {
            _ = window.RunSmokeTestAsync(output, e.Args.Contains("--online"), e.Args.Contains("--speedtest-cn"), e.Args.Contains("--check-github"));
        }
    }
}
