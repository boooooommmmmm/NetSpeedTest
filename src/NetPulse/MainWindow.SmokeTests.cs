using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetPulse.Core;

namespace NetPulse;

public partial class MainWindow
{
    private async Task VerifyUpdateWorkflowAsync(string outputDirectory, Dictionary<string, object> report)
    {
        var fixture = new GitHubReleaseInfo("99.0.0", $"https://github.com/{GitHubUpdateClient.Repository}/releases/download/v99.0.0/NetPulse.exe",
            $"https://github.com/{GitHubUpdateClient.Repository}/releases/tag/v99.0.0", 100, new string('A', 64), "测试发布说明", DateTimeOffset.UtcNow);
        var fail = false; var calls = 0;
        Task<GitHubReleaseInfo?> Check(CancellationToken token)
        {
            calls++;
            return fail ? Task.FromException<GitHubReleaseInfo?>(new IOException("模拟 GitHub 离线")) : Task.FromResult<GitHubReleaseInfo?>(fixture);
        }
        var path = Path.Combine(outputDirectory, "update-simulation");
        var window = new MainWindow(path, automaticStartup: false, releaseChecker: Check);
        try
        {
            await window.CheckUpdatesAsync(force: true);
            if (!window.CanDownloadUpdate || !window.UpdateStatus.Contains("99.0.0") || calls != 1)
                throw new InvalidOperationException("A newer GitHub release was not shown.");
            await window.CheckUpdatesAsync(force: false);
            if (calls != 1) throw new InvalidOperationException("Periodic update check ignored the six-hour interval.");
            window._speedCts = new();
            await window.CheckUpdatesAsync(force: true);
            window._speedCts.Dispose(); window._speedCts = null;
            if (calls != 1) throw new InvalidOperationException("Update check ran during a speed test.");
            fail = true;
            await window.CheckUpdatesAsync(force: true);
            if (!window.CanDownloadUpdate || !window.UpdateStatus.Contains("失败") || calls != 2)
                throw new InvalidOperationException("A failed check discarded a previously discovered update.");
            window.AutoUpdateCheckBox.IsChecked = false;
            var reopened = new MainWindow(path, automaticStartup: false, releaseChecker: Check);
            try
            {
                if (reopened.AutoUpdateCheckBox.IsChecked != false || !reopened.CanDownloadUpdate)
                    throw new InvalidOperationException("Update preference or cached release did not survive reopening.");
                await reopened.CheckUpdatesAsync(force: false);
                if (calls != 2) throw new InvalidOperationException("Disabled automatic update check still ran.");
            }
            finally { reopened.Close(); }
            report["gitHubUpdateWorkflow"] = new { NewVersionVisible = true, CheckInterval = true, BusyDeferral = true, FailureRetention = true, PreferenceRestore = true };
        }
        finally { window.Close(); }
    }

    private async Task VerifySynchronizationAsync(string outputDirectory, Dictionary<string, object> report, NodeCatalogProvider provider)
    {
        var storage = new NodeCatalogStorage(Path.Combine(outputDirectory, "sync-simulation-" + provider), provider);
        var protocol = provider == NodeCatalogProvider.SpeedTestNet ? SpeedProtocol.SpeedTestNet : SpeedProtocol.SpeedTestCn;
        var nodeA = new SpeedTestNode("local-a", "回环测试 A", "", "", "", new("http://127.0.0.1:9/download"), new("http://127.0.0.1:9/upload"), new("http://127.0.0.1:9/hello"), 1, protocol);
        var nodeB = nodeA with { Id = "local-b", Name = "回环测试 B" };
        var snapshot = new NodeCatalogSnapshot(DateTimeOffset.UtcNow.AddMinutes(-20), false,
            [new(nodeA, 1, ["127.0.0.1"]), new(nodeB, 2, ["127.0.0.1"])], "local-b", Provider: provider);
        storage.SaveSettings(new(Provider: provider)); storage.SaveCatalog(snapshot);
        var calls = 0;
        var simulateFailure = true;
        Task<IReadOnlyList<SpeedTestNode>> Fetch(bool proxy, CancellationToken token)
        {
            calls++;
            return simulateFailure ? Task.FromException<IReadOnlyList<SpeedTestNode>>(new IOException("模拟离线"))
                : Task.FromResult<IReadOnlyList<SpeedTestNode>>([nodeB, nodeA]);
        }
        var window = new MainWindow(storage.DirectoryPath, automaticStartup: false, nodeCatalogFetcher: Fetch);
        try
        {
            if (window.NodeBox.SelectedItem is not RankedSpeedNode { Node.Id: "local-b" } || window.SyncedIpRows.Count != 2)
                throw new InvalidOperationException("Cached node selection or IP list was not restored.");
            window.DownloadValue = "123.4"; window.UploadValue = "56.7";
            await window.CheckAutoSyncAsync();
            if (calls != 1 || window._nodeSnapshot?.SyncedAt != snapshot.SyncedAt || !window.SyncStatus.Contains("同步失败") || window.DownloadValue != "123.4")
                throw new InvalidOperationException("A failed sync discarded cached nodes or speed results.");
            await window.CheckAutoSyncAsync();
            if (calls != 1) throw new InvalidOperationException("Failed sync retried without backoff.");
            window._syncSchedule.RequestNow();
            window._speedCts = new();
            await window.CheckAutoSyncAsync();
            window._speedCts.Dispose(); window._speedCts = null;
            if (calls != 1 || !window._syncSchedule.IsDue(DateTimeOffset.UtcNow))
                throw new InvalidOperationException("Automatic sync was not deferred while speed testing.");
            simulateFailure = false;
            await window.CheckAutoSyncAsync();
            if (calls != 2 || window._syncSchedule.IsDue(DateTimeOffset.UtcNow) || window.DownloadValue != "123.4" || window.UploadValue != "56.7" ||
                window.NodeBox.SelectedItem is not RankedSpeedNode { Node.Id: "local-b", IpAddresses.Count: > 0 })
                throw new InvalidOperationException("Successful periodic sync did not preserve selection and results.");
            window.AutoSyncBox.IsChecked = false;
            window.SyncIntervalBox.SelectedIndex = 2;
            window._syncSchedule.RequestNow();
            await window.CheckAutoSyncAsync();
            if (calls != 2 || !window.SyncStatus.Contains("已关闭")) throw new InvalidOperationException("Disabled automatic sync still ran.");
            var reopened = new MainWindow(storage.DirectoryPath, automaticStartup: false, nodeCatalogFetcher: Fetch);
            try
            {
                if (reopened.AutoSyncBox.IsChecked != false || Selected(reopened.SyncIntervalBox) != 30 ||
                    reopened.NodeBox.SelectedItem is not RankedSpeedNode { Node.Id: "local-b" })
                    throw new InvalidOperationException("Sync settings and nodes did not survive reopening.");
            }
            finally { reopened.Close(); }
            var otherIndex = provider == NodeCatalogProvider.SpeedTestNet ? 0 : 3;
            var originalIndex = provider == NodeCatalogProvider.SpeedTestNet ? 3 : 0;
            window.SourceBox.SelectedIndex = otherIndex;
            if (window.NodeBox.Items.Count != 0 || window._nodeSnapshot != null || window.SyncedIpRows.Count != 0)
                throw new InvalidOperationException("Switching providers retained nodes from another catalog.");
            window.SourceBox.SelectedIndex = originalIndex;
            if (window.NodeBox.SelectedItem is not RankedSpeedNode { Node.Id: "local-b" } || window._nodeStorage.Provider != provider)
                throw new InvalidOperationException("Switching back did not restore the provider cache.");
            report["periodicSynchronization" + provider] = new { CacheRestore = true, FailureRetention = true, FailureBackoff = true, DeferralDuringSpeedTest = true, SelectionPreserved = true, ResultsPreserved = true, SettingsRestore = true, ProviderIsolation = true };
        }
        finally { window.Close(); }
    }

    private void VerifySpeedSummaryIsolation(string outputDirectory, Dictionary<string, object> report)
    {
        var window = new MainWindow(Path.Combine(outputDirectory, "summary-simulation"), automaticStartup: false);
        try
        {
            foreach (var phase in Enum.GetValues<TrafficPhase>())
                window.AddSample(new(DateTimeOffset.UtcNow, "prior-run", ProbeMode.Tcp, phase, 1000, "previous test"));
            var firstSample = window._samples.Count;
            window.AddSample(new(DateTimeOffset.UtcNow, "current-run", ProbeMode.Tcp, TrafficPhase.Idle, 10, ""));
            window.AddSample(new(DateTimeOffset.UtcNow, "current-run", ProbeMode.Tcp, TrafficPhase.Download, 20, ""));
            window.AddSample(new(DateTimeOffset.UtcNow, "current-run", ProbeMode.Tcp, TrafficPhase.Upload, 30, ""));
            window.AddSample(new(DateTimeOffset.UtcNow, "current-run", ProbeMode.Tcp, TrafficPhase.Download, null, "timeout"));
            var summary = window.BuildSummary(firstSample);
            if (!summary.Contains($"空闲 {10:F1} / 下载 {20:F1} / 上传 {30:F1} ms") || window._samples.Count != 7)
                throw new InvalidOperationException("Current speed summary mixed in prior tests or cleared monitor history: " + summary);
            if (!window.BuildSummary(window._samples.Count).Contains("空闲 — / 下载 — / 上传 — ms"))
                throw new InvalidOperationException("Empty current test reused historical latency samples.");
            report["speedSummaryIsolation"] = new { CurrentRunOnly = true, MonitorHistoryPreserved = true, EmptyPhases = true };
        }
        finally { window.Close(); }
    }

    public async Task RunSmokeTestAsync(string outputDirectory, bool online, bool speedTestCn = false, bool checkGitHub = false, bool speedTestNet = false)
    {
        Directory.CreateDirectory(outputDirectory);
        var report = new Dictionary<string, object>();
        try
        {
            await Task.Delay(600);
            VerifySpeedSummaryIsolation(outputDirectory, report);
            await VerifyUpdateWorkflowAsync(outputDirectory, report);
            await VerifySynchronizationAsync(outputDirectory, report, NodeCatalogProvider.SpeedTestCn);
            await VerifySynchronizationAsync(outputDirectory, report, NodeCatalogProvider.SpeedTestNet);
            SaveScreenshot(Path.Combine(outputDirectory, "idle.png"));
            SourceBox.SelectedIndex = 1;
            if (!EndpointsReadOnly || !DownloadUrlBox.Text.Contains("cloudflare.com")) throw new InvalidOperationException("Cloudflare preset failed.");
            SourceBox.SelectedIndex = 2;
            if (EndpointsReadOnly) throw new InvalidOperationException("Custom source should be editable.");
            SourceBox.SelectedIndex = 0;
            if (!EndpointsReadOnly || NodePickerVisibility != Visibility.Visible) throw new InvalidOperationException("SpeedTest.cn preset failed.");
            SourceBox.SelectedIndex = 3;
            if (!EndpointsReadOnly || NodePickerVisibility != Visibility.Visible || _nodeStorage.Provider != NodeCatalogProvider.SpeedTestNet)
                throw new InvalidOperationException("Speedtest.net preset failed.");
            report["sourceSelection"] = "passed";
            TargetBox.Text = "127.0.0.1";
            StartMonitor();
            await Task.Delay(2200);
            await StopMonitorAsync();
            if (_samples.Count < 2 || _stats.Received < 2) throw new InvalidOperationException("Loopback monitor failed.");
            report["monitor"] = new { _stats.Total, _stats.Received, _stats.Jitter, _stats.LossPercent };
            File.WriteAllText(Path.Combine(outputDirectory, "loopback.csv"), CsvExporter.Export(_samples), new UTF8Encoding(true));
            SaveScreenshot(Path.Combine(outputDirectory, "monitor.png"));
            // Exercise the actual UI workflow, cancellation during baseline, and reset.
            var cancellationRun = RunSpeedTestAsync(new(new("https://speed.cloudflare.com/__down"), null, 1, 1, 1024 * 1024));
            await Task.Delay(150);
            _speedCts!.Cancel();
            await cancellationRun;
            if (_speedCts != null || _monitorCts != null || DownloadValue != "—") throw new InvalidOperationException("Cancellation did not reset state.");
            report["cancellation"] = "passed";
            Clear_Click(this, new());
            if (HasSamples || DownloadValue != "—") throw new InvalidOperationException("Clear failed.");
            report["clear"] = "passed";
            if (online || speedTestCn || speedTestNet)
            {
                TargetBox.Text = "1.1.1.1";
                ModeBox.SelectedIndex = 1;
                SourceBox.SelectedIndex = speedTestNet ? 3 : speedTestCn ? 0 : 1;
                if (speedTestCn || speedTestNet)
                {
                    var loading = LoadNodesAsync();
                    if (_nodeCts == null || SourceSettingsEnabled) throw new InvalidOperationException("Node loading should lock source settings.");
                    _nodeCts.Cancel();
                    try { await loading; throw new InvalidOperationException("Node cancellation failed."); }
                    catch (OperationCanceledException) { }
                    if (_nodeCts != null || !SourceSettingsEnabled) throw new InvalidOperationException("Node cancellation did not restore controls.");
                    report["nodeCancellation"] = "passed";
                }
                var setup = await CreateSpeedOptionsAsync();
                if (speedTestCn || speedTestNet)
                {
                    if (_nodeSnapshot == null || !SyncedIpRows.Any(r => r.Addresses != "未解析") || _nodeStorage.LoadCatalog()?.Nodes.Count != _nodeSnapshot.Nodes.Count)
                        throw new InvalidOperationException("Live node IP synchronization or cache save failed.");
                    SyncedNodesExpander.IsExpanded = true;
                    SaveScreenshot(Path.Combine(outputDirectory, "synchronized-ips.png"));
                    SyncedNodesExpander.IsExpanded = false;
                    report["liveSynchronization"] = new { Nodes = _nodeSnapshot.Nodes.Count, Resolved = _nodeSnapshot.Nodes.Count(n => n.IpAddresses is { Count: > 0 }), LastSuccess = _nodeSnapshot.SyncedAt, NextAttempt = _syncSchedule.NextAttempt };
                }
                await RunSpeedTestAsync(setup.Options with { DurationSeconds = 5, Connections = 4, MaxBytesPerDirection = 32L * 1024 * 1024 }, setup.Label);
                report["online"] = new { Source = setup.Label, DownloadValue, UploadValue, StatusText, StatusDetail, _stats.Total };
                File.WriteAllText(Path.Combine(outputDirectory, "online.csv"), CsvExporter.Export(_samples), new UTF8Encoding(true));
                SaveScreenshot(Path.Combine(outputDirectory, "online.png"));
                if (DownloadValue is "失败" or "—" || UploadValue is "失败" or "—") throw new InvalidOperationException("Online speed test failed: " + StatusDetail);
                SourceBox.SelectedIndex = SourceBox.SelectedIndex == 0 ? 1 : 0;
                if (DownloadValue != "—" || UploadValue != "—") throw new InvalidOperationException("Source switch retained results from the previous source.");
                report["sourceResultReset"] = "passed";
            }
            if (checkGitHub)
            {
                await CheckUpdatesAsync(force: true);
                report["liveGitHubUpdateCheck"] = new { Current = CurrentVersion.ToString(3), Latest = _updateState.Release?.Version, UpdateStatus };
                SaveScreenshot(Path.Combine(outputDirectory, "github-update.png"));
                if (_updateState.Release == null || UpdateStatus.Contains("失败")) throw new InvalidOperationException("Live GitHub release check failed.");
            }
            report["success"] = true;
        }
        catch (Exception ex) { report["success"] = false; report["error"] = ex.ToString(); Environment.ExitCode = 1; }
        finally
        {
            await StopMonitorAsync();
            File.WriteAllText(Path.Combine(outputDirectory, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
    }

    private void SaveScreenshot(string path)
    {
        UpdateLayout();
        var content = (FrameworkElement)Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        bitmap.Render(visual);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
