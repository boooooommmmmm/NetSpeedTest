using System.IO;
using System.Windows;
using NetPulse.Core;

namespace NetPulse;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--apply-update")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { Shutdown(e.Args.Length == 3 ? await SelfUpdate.RunHelperAsync(e.Args[1], e.Args[2]) : 1); }
            catch { Shutdown(1); }
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("操作未完成：" + args.Exception.GetBaseException().Message, "NetPulse", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var smokeIndex = Array.IndexOf(e.Args, "--smoke-test");
        var output = smokeIndex >= 0 && smokeIndex + 1 < e.Args.Length ? e.Args[smokeIndex + 1] : Path.Combine(Path.GetTempPath(), "NetPulse-smoke");
        var testIndex = Array.IndexOf(e.Args, "--self-update-test");
        var restartedIndex = Array.IndexOf(e.Args, "--self-update-restarted");
        var completeIndex = Array.IndexOf(e.Args, "--update-complete");
        var rollbackIndex = Array.IndexOf(e.Args, "--update-rolled-back");
        var contextIndex = Math.Max(completeIndex, rollbackIndex);
        var requestPath = Environment.GetEnvironmentVariable("NETPULSE_UPDATE_REQUEST");
        var requestNonce = Environment.GetEnvironmentVariable("NETPULSE_UPDATE_NONCE");
        var rolledBack = Environment.GetEnvironmentVariable("NETPULSE_UPDATE_ROLLBACK") == "1" || rollbackIndex >= 0;
        var updateCompleted = !rolledBack && (requestPath != null || completeIndex >= 0);
        foreach (var key in new[] { "NETPULSE_UPDATE_REQUEST", "NETPULSE_UPDATE_NONCE", "NETPULSE_UPDATE_ROLLBACK" })
            Environment.SetEnvironmentVariable(key, null);
        SelfUpdateTransaction? transaction = null;
        string? dataDirectory = smokeIndex >= 0 ? Path.Combine(output, "appdata") : null;
        if (contextIndex >= 0 || requestPath != null)
        {
            try
            {
                if (contextIndex >= 0)
                {
                    if (contextIndex + 2 >= e.Args.Length) throw new InvalidDataException("缺少更新事务参数。");
                    requestPath = e.Args[contextIndex + 1]; requestNonce = e.Args[contextIndex + 2];
                }
                transaction = SelfUpdate.ReadTransaction(requestPath!, requestNonce ?? "");
                dataDirectory = Path.GetDirectoryName(Path.GetDirectoryName(transaction.DirectoryPath));
                if (rolledBack)
                {
                    var storage = new UpdateStateStorage(dataDirectory!);
                    storage.Save(storage.Load() with { RetryAfter = DateTimeOffset.UtcNow.AddMinutes(15) });
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "更新恢复失败"); Shutdown(1); return; }
        }
        if (testIndex >= 0 && testIndex + 3 < e.Args.Length) dataDirectory = Path.Combine(e.Args[testIndex + 1], "appdata");
        if (restartedIndex >= 0 && updateCompleted && e.Args.ElementAtOrDefault(restartedIndex + 2) == "startup-failure")
        { Shutdown(42); return; }
        var window = new MainWindow(dataDirectory, automaticStartup: smokeIndex < 0 && testIndex < 0 && restartedIndex < 0);
        MainWindow = window;
        var startupReported = false;
        window.ContentRendered += async (_, _) =>
        {
            if (startupReported) return;
            startupReported = true;
            try
            {
                if (transaction != null)
                {
                    if (updateCompleted) await SelfUpdate.AcknowledgeStartupAsync(transaction);
                    window.ReportUpdateStartup(transaction, rolledBack);
                    _ = Task.Run(() => SelfUpdate.CleanupAfterHelperAsync(transaction));
                }
                if (restartedIndex >= 0 && restartedIndex + 2 < e.Args.Length)
                {
                    window.WriteSelfUpdateProof(e.Args[restartedIndex + 1], rolledBack ? "RolledBack" : "Succeeded", transaction);
                    await Task.Delay(1000);
                    window.Close();
                }
            }
            catch { Shutdown(1); }
        };
        window.Show();
        if (testIndex >= 0 && testIndex + 3 < e.Args.Length)
            _ = window.RunSelfUpdateScenarioAsync(e.Args[testIndex + 1], e.Args[testIndex + 2], e.Args[testIndex + 3]);
        if (e.Args.Contains("--smoke-test"))
        {
            _ = window.RunSmokeTestAsync(output, e.Args.Contains("--online"), e.Args.Contains("--speedtest-cn"), e.Args.Contains("--check-github"), e.Args.Contains("--speedtest-net"));
        }
    }
}
