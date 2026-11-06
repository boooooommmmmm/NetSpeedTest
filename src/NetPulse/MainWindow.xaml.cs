using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using NetPulse.Core;

namespace NetPulse;

public sealed record SampleRow(string Time, string Target, string Mode, string Phase, string Latency, string Detail);
public sealed record SyncedIpRow(string Name, string Host, string Addresses, string Latency);

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int MaxSamples = 100_000;
    private readonly List<ProbeSample> _samples = [];
    private readonly List<PlotPoint> _speedPoints = [];
    private LatencyStatistics _stats = new();
    private CancellationTokenSource? _monitorCts, _speedCts, _nodeCts;
    private bool _ready, _loadingNodes, _nodesUseProxy;
    private NodeCatalogStorage _nodeStorage;
    private readonly Func<bool, CancellationToken, Task<IReadOnlyList<SpeedTestNode>>> _fetchNodeCatalog;
    private NodeCatalogSnapshot? _nodeSnapshot;
    private NodeSyncSchedule _syncSchedule;
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _applyingCatalog;
    private string? _syncMessage;
    private string _nodeStatus = "自动同步 Speedtest.net 节点；也可点击立即同步后手动选择。";
    private string _activeSourceLabel = "";
    private Task? _monitorTask;
    private TrafficPhase _phase;
    private readonly Stopwatch _session = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _closing;
    private bool _automaticMonitor;
    private long _downloadBytes, _uploadBytes;
    private string _downloadValue = "—", _uploadValue = "—", _latencyValue = "—", _jitterValue = "—";
    private string _downloadHint = "尚未测速", _uploadHint = "尚未测速";
    private string _statusText = "准备就绪", _statusDetail = "开始测速，或单独开启持续监测。", _elapsedText = "00:00:00";
    private double _progressValue;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<SampleRow> RecentSamples { get; } = [];
    public ObservableCollection<SyncedIpRow> SyncedIpRows { get; } = [];
    private bool IsNodeSource => SourceBox.SelectedIndex is 0 or 3;
    private string CatalogSourceName => _nodeStorage.Provider == NodeCatalogProvider.SpeedTestNet ? "Speedtest.net" : "测速网 SpeedTest.cn";
    public string NodePickerLabel => CatalogSourceName + " 节点 · 自动检测可达性，也可手动选择";
    public string AutoSyncLabel => "自动同步 " + CatalogSourceName + " 节点 / IP";
    public string SyncedNodesHeader => $"{CatalogSourceName} 已同步的测速 IP 列表（{SyncedIpRows.Count} 个节点）";
    public string SyncStatus
    {
        get
        {
            var last = CatalogSourceName + " · " + (_nodeSnapshot == null ? "尚未成功同步" : $"上次成功 {_nodeSnapshot.SyncedAt.LocalDateTime:MM-dd HH:mm:ss} · {_nodeSnapshot.Nodes.Count} 个节点 / {_nodeSnapshot.Nodes.SelectMany(n => n.IpAddresses ?? []).Distinct().Count()} 个 IP");
            if (_loadingNodes) return last + " · 正在同步…";
            if (!string.IsNullOrEmpty(_syncMessage)) return last + " · " + _syncMessage;
            if (!_syncSchedule.Settings.Enabled) return last + " · 自动同步已关闭";
            if (_syncSchedule.IsDue(DateTimeOffset.UtcNow)) return last + (_speedCts != null ? " · 测速完成后同步" : " · 等待同步");
            return last + $" · 下次 {_syncSchedule.NextAttempt?.LocalDateTime:HH:mm:ss}";
        }
    }
    public bool MonitorSettingsEnabled => _monitorCts == null && _speedCts == null && !_loadingNodes && _updateDownloadCts == null;
    public bool SpeedSettingsEnabled => _speedCts == null && !_loadingNodes && _updateDownloadCts == null;
    public bool SourceSettingsEnabled => _speedCts == null && !_loadingNodes && _updateDownloadCts == null;
    public bool EndpointsReadOnly => SourceBox.SelectedIndex != 2;
    public Visibility NodePickerVisibility => IsNodeSource ? Visibility.Visible : Visibility.Collapsed;
    public string NodeStatus { get => _nodeStatus; set => Set(ref _nodeStatus, value); }
    public bool CanClear => _monitorCts == null && _speedCts == null && _samples.Count > 0;
    public bool HasSamples => _samples.Count > 0;
    public string MonitorButtonText => _monitorCts == null ? "开始监测" : "停止监测";
    public string SpeedButtonText => _loadingNodes ? "取消选点" : _speedCts == null ? "开始测速" : "停止测速";
    public string DownloadValue { get => _downloadValue; set => Set(ref _downloadValue, value); }
    public string UploadValue { get => _uploadValue; set => Set(ref _uploadValue, value); }
    public string LatencyValue { get => _latencyValue; set => Set(ref _latencyValue, value); }
    public string JitterValue { get => _jitterValue; set => Set(ref _jitterValue, value); }
    public string DownloadHint { get => _downloadHint; set => Set(ref _downloadHint, value); }
    public string UploadHint { get => _uploadHint; set => Set(ref _uploadHint, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string StatusDetail { get => _statusDetail; set => Set(ref _statusDetail, value); }
    public string ElapsedText { get => _elapsedText; set => Set(ref _elapsedText, value); }
    public double ProgressValue { get => _progressValue; set => Set(ref _progressValue, value); }
    public string AverageHint => _stats.Average is { } average ? $"平均 {average:F1} ms · P95 {_stats.P95:F1} ms" : "等待有效延迟样本";
    public string LossHint => _stats.Total == 0 ? "丢包 / 失败率 —" : $"丢包 / 失败率 {_stats.LossPercent:F1}% · {_stats.Lost}/{_stats.Total}";
    public string RangeHint => _stats.Minimum is { } min ? $"最低 {min:F1} ms    最高 {_stats.Maximum:F1} ms    抖动 = 相邻成功采样的延迟差均值" : "抖动根据连续成功采样计算；失败会中断相邻采样对。";
    public string SampleCountText => $"{_stats.Total:N0} 次探测 · 列表显示最近 60 条 · CSV 包含本次全部记录（最多 100,000 条）";
    public string TrafficText => $"本次有效数据  ↓ {FormatBytes(_downloadBytes)}   ↑ {FormatBytes(_uploadBytes)}";

    public MainWindow(string? dataDirectory = null, bool automaticStartup = true,
        Func<bool, CancellationToken, Task<IReadOnlyList<SpeedTestNode>>>? nodeCatalogFetcher = null,
        Func<CancellationToken, Task<GitHubReleaseInfo?>>? releaseChecker = null)
    {
        var directory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPulse");
        var syncSettings = new NodeCatalogStorage(directory).LoadSettings();
        _nodeStorage = new(directory, syncSettings.Provider);
        _allowAutomaticInstallation = automaticStartup;
        _fetchNodeCatalog = nodeCatalogFetcher ?? ((proxy, token) => _nodeStorage.Provider == NodeCatalogProvider.SpeedTestNet
            ? new SpeedTestNetClient().GetNodesAsync(proxy, token) : new SpeedTestCnClient().GetNodesAsync(proxy, token));
        _nodeSnapshot = _nodeStorage.LoadCatalog();
        _syncSchedule = new(syncSettings, _nodeSnapshot?.SyncedAt);
        if (_nodeSnapshot != null && _nodeSnapshot.UseProxy != syncSettings.UseProxy) _syncSchedule.RequestNow();
        InitializeComponent();
        DataContext = this;
        SourceBox.SelectedIndex = syncSettings.Provider == NodeCatalogProvider.SpeedTestNet ? 3 : 0;
        AutoSyncBox.IsChecked = syncSettings.Enabled;
        SyncIntervalBox.SelectedIndex = Array.IndexOf(new[] { 5, 15, 30, 60 }, syncSettings.IntervalMinutes);
        ProxyBox.IsChecked = syncSettings.UseProxy;
        InitializeUpdates(releaseChecker);
        if (_nodeSnapshot != null) ApplyCatalog(_nodeSnapshot);
        _ready = true;
        UpdateSourceUi();
        Height = Math.Clamp(SystemParameters.WorkArea.Height - 30, 700, 930);
        _clock.Tick += (_, _) => ElapsedText = _session.Elapsed.ToString(@"hh\:mm\:ss");
        _clock.Start();
        _syncTimer.Tick += async (_, _) => await CheckAutoSyncAsync();
        if (automaticStartup) Loaded += async (_, _) => { _syncTimer.Start(); _updateTimer.Start(); await CheckAutoSyncAsync(); await CheckUpdatesAsync(force: false); await TryAutoInstallAsync(); };
        SourceInitialized += (_, _) =>
        {
            var dark = 1;
            _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
        Closing += (_, args) =>
        {
            if (_updateDownloadCts != null && !_updateExitRequested)
            {
                args.Cancel = true;
                if (!_updateHandoff) { _closeAfterUpdateCancel = true; _updateDownloadCts.Cancel(); }
                return;
            }
            _closing = true;
            _clock.Stop();
            _syncTimer.Stop();
            _updateTimer.Stop();
            _updateCheckCts?.Cancel();
            _updateDownloadCts?.Cancel();
            _speedCts?.Cancel();
            _monitorCts?.Cancel();
            _nodeCts?.Cancel();
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; Notify(property!); }
    private void Notify(params string[] properties)
    { foreach (var property in properties) PropertyChanged?.Invoke(this, new(property)); }
    private void RefreshControls()
    {
        Notify(nameof(MonitorSettingsEnabled), nameof(SpeedSettingsEnabled), nameof(SourceSettingsEnabled), nameof(CanClear), nameof(HasSamples), nameof(MonitorButtonText), nameof(SpeedButtonText));
        RefreshUpdateControls();
    }
    private static int Selected(ComboBox box) => int.Parse(((ComboBoxItem)box.SelectedItem).Tag.ToString()!, CultureInfo.InvariantCulture);
    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576d:F1} MiB" : $"{bytes / 1024d:F1} KiB";
    private static string Number(double value) => value.ToString(value >= 1000 ? "F0" : "F1", CultureInfo.CurrentCulture);

    private async Task<(SpeedOptions Options, string Label)> CreateSpeedOptionsAsync()
    {
        var source = SourceBox.SelectedIndex;
        if (IsNodeSource && (NodeBox.SelectedItem == null || _nodesUseProxy != (ProxyBox.IsChecked == true)))
        {
            try { await LoadNodesAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException && NodeBox.SelectedItem != null)
            { /* An explicit speed test can continue with the retained catalog. */ }
        }
        if (IsNodeSource && NodeBox.SelectedItem == null)
            throw new InvalidOperationException($"没有可用的 {CatalogSourceName} 节点，请先同步或更换测速源。");
        var downloadUri = SpeedOptions.ValidateUri(new Uri(DownloadUrlBox.Text.Trim(), UriKind.Absolute));
        var uploadUri = string.IsNullOrWhiteSpace(UploadUrlBox.Text) ? null : SpeedOptions.ValidateUri(new Uri(UploadUrlBox.Text.Trim(), UriKind.Absolute));
        var options = new SpeedOptions(downloadUri, uploadUri, Selected(DurationBox), Selected(ConnectionsBox), Selected(LimitBox) * 1024L * 1024,
            ProxyBox.IsChecked == true, IsNodeSource && NodeBox.SelectedItem is RankedSpeedNode node ? node.Node.Protocol : SpeedProtocol.BytesQuery);
        options.Validate();
        var label = IsNodeSource && NodeBox.SelectedItem is RankedSpeedNode selected ? CatalogSourceName + " · " + selected.Node.DisplayName
            : source == 1 ? "Cloudflare" : "自定义 · " + downloadUri.Host;
        return (options, label);
    }

    private async void Monitor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_monitorCts != null) { _automaticMonitor = false; await StopMonitorAsync(); return; }
            if (_speedCts != null || _updateDownloadCts != null) return;
            StartMonitor();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void StartMonitor()
    {
        var host = ProbeService.ValidateHost(TargetBox.Text);
        var mode = ModeBox.SelectedIndex == 0 ? ProbeMode.Icmp : ProbeMode.Tcp;
        var port = 443;
        if (mode == ProbeMode.Tcp && (!int.TryParse(PortBox.Text, out port) || port is < 1 or > 65535))
            throw new ArgumentException("TCP 端口应为 1 到 65535 之间的整数。");
        ResetSamples();
        var interval = Selected(IntervalBox);
        var timeout = Selected(TimeoutBox);
        _monitorCts = new();
        var token = _monitorCts.Token;
        _session.Restart();
        StatusText = "正在监测";
        StatusDetail = $"{host} · {(mode == ProbeMode.Icmp ? "ICMP Ping" : $"TCP :{port}")} · 间隔 {interval / 1000d:0.#} 秒";
        RefreshControls();
        _monitorTask = MonitorLoopAsync(host, mode, port, timeout, interval, token);
    }

    private async Task MonitorLoopAsync(string host, ProbeMode mode, int port, int timeout, int interval, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _samples.Count < MaxSamples)
            {
                var started = Stopwatch.GetTimestamp();
                var sample = await ProbeService.ProbeAsync(host, mode, port, timeout, _phase, token);
                if (_closing || token.IsCancellationRequested) break;
                AddSample(sample);
                var remainder = interval - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (remainder > 0) await Task.Delay(TimeSpan.FromMilliseconds(remainder), token);
            }
            if (_samples.Count >= MaxSamples)
            {
                StatusText = "监测已达到记录上限";
                StatusDetail = "已保留 100,000 条记录，请导出 CSV 后开启新会话。";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!_closing) ShowError(ex); }
        finally
        {
            _monitorCts?.Dispose();
            _monitorCts = null;
            _session.Stop();
            if (!_closing) RefreshControls();
        }
    }

    private async Task StopMonitorAsync()
    {
        _monitorCts?.Cancel();
        if (_monitorTask != null) await _monitorTask;
        if (!_closing && _speedCts == null)
        { StatusText = "监测已停止"; StatusDetail = "记录已保留，可导出 CSV；再次开始监测将建立新会话。"; }
    }

    private void AddSample(ProbeSample sample)
    {
        _samples.Add(sample);
        _stats.Add(sample);
        LatencyValue = sample.LatencyMs is { } value ? Number(value) : "失败";
        JitterValue = _stats.Jitter is { } jitter ? Number(jitter) : "—";
        RecentSamples.Insert(0, new(sample.Time.ToString("HH:mm:ss"), sample.Target,
            sample.Mode == ProbeMode.Icmp ? "ICMP" : "TCP", sample.PhaseLabel,
            sample.LatencyMs is { } latency ? $"{latency:F1} ms" : "失败", sample.Detail));
        if (RecentSamples.Count > 60) RecentSamples.RemoveAt(60);
        LatencyPlot.SetPoints(_samples.TakeLast(180).Select(s => new PlotPoint(s.Time, s.LatencyMs, s.Phase == TrafficPhase.Upload)));
        Notify(nameof(AverageHint), nameof(LossHint), nameof(RangeHint), nameof(SampleCountText), nameof(HasSamples), nameof(CanClear));
    }

    private async void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (_updateDownloadCts != null) return;
        if (_nodeCts != null) { _nodeCts.Cancel(); return; }
        if (_speedCts != null) { _speedCts.Cancel(); StatusText = "正在停止测速…"; return; }
        try
        {
            var setup = await CreateSpeedOptionsAsync();
            if (!_closing) await RunSpeedTestAsync(setup.Options, setup.Label);
        }
        catch (OperationCanceledException) { NodeStatus = "已取消选点，可以重新开始。"; }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task RunSpeedTestAsync(SpeedOptions options, string sourceLabel = "Cloudflare")
    {
        if (_monitorCts?.IsCancellationRequested == true && _monitorTask != null) await _monitorTask;
        _automaticMonitor = _monitorCts == null;
        if (_automaticMonitor) StartMonitor();
        var firstSample = _samples.Count;
        _activeSourceLabel = sourceLabel;
        _speedCts = new();
        var token = _speedCts.Token;
        DownloadValue = UploadValue = "—";
        DownloadHint = "等待下载测试";
        UploadHint = options.UploadUri == null ? "已跳过上传" : "等待上传测试";
        _downloadBytes = _uploadBytes = 0;
        _speedPoints.Clear();
        SpeedPlot.SetPoints([]);
        ProgressValue = 0;
        RefreshControls();
        var completed = new List<SpeedResult>();
        var failures = new List<string>();
        var canceled = false;
        try
        {
            StatusText = "采集空闲延迟…";
            StatusDetail = "先采集 3 秒空闲样本，再依次测试下载和上传。";
            await Task.Delay(3000, token);
            var service = new SpeedTestService();
            foreach (var phase in options.UploadUri == null ? new[] { TrafficPhase.Download } : new[] { TrafficPhase.Download, TrafficPhase.Upload })
            {
                token.ThrowIfCancellationRequested();
                _phase = phase;
                var label = phase == TrafficPhase.Download ? "下载" : "上传";
                StatusText = $"正在测试{label}…";
                StatusDetail = $"{_activeSourceLabel} · {options.Connections} 条连接 · 最长 {options.DurationSeconds} 秒 · 上限 {FormatBytes(options.MaxBytesPerDirection)}";
                ProgressValue = 0;
                var progress = new Progress<SpeedProgress>(p =>
                {
                    if (_closing || _speedCts == null || token.IsCancellationRequested || _phase != p.Phase) return;
                    var speed = Number(p.MegabitsPerSecond);
                    if (p.Phase == TrafficPhase.Download) { DownloadValue = speed; _downloadBytes = p.Bytes; DownloadHint = "实时速度 · " + FormatBytes(p.Bytes); }
                    else { UploadValue = speed; _uploadBytes = p.Bytes; UploadHint = "已确认数据 · " + FormatBytes(p.Bytes); }
                    ProgressValue = p.Fraction * 100;
                    _speedPoints.Add(new(DateTimeOffset.Now, p.MegabitsPerSecond, p.Phase == TrafficPhase.Upload));
                    SpeedPlot.SetPoints(_speedPoints);
                    Notify(nameof(TrafficText));
                });
                try
                {
                    var result = await service.RunPhaseAsync(options, phase, progress, token);
                    completed.Add(result);
                    var hint = $"均值 · {result.MegabitsPerSecond / 8:F1} MB/s";
                    if (result.ReachedLimit) hint += " · 达流量上限";
                    if (result.Errors.Count > 0) { hint += " · 部分连接失败"; failures.Add(label + "部分连接失败：" + string.Join("；", result.Errors)); }
                    if (phase == TrafficPhase.Download) { DownloadValue = Number(result.MegabitsPerSecond); DownloadHint = hint; _downloadBytes = result.Bytes; }
                    else { UploadValue = Number(result.MegabitsPerSecond); UploadHint = hint; _uploadBytes = result.Bytes; }
                    Notify(nameof(TrafficText));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add(label + "：" + ex.GetBaseException().Message);
                    if (phase == TrafficPhase.Download) { DownloadValue = "失败"; DownloadHint = "请检查节点或网络"; }
                    else { UploadValue = "失败"; UploadHint = "请检查节点或网络"; }
                }
            }
            _phase = TrafficPhase.Idle;
            StatusText = "采集恢复延迟…";
            await Task.Delay(1500, token);
            ProgressValue = 100;
            StatusText = failures.Count > 0 ? (completed.Count > 0 ? "测速部分完成" : "测速未完成") : "测速完成";
            StatusDetail = failures.Count > 0 ? string.Join("；", failures) : BuildSummary(firstSample);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            canceled = true;
            StatusText = "测速已停止";
            StatusDetail = "已完成的项目和探测记录已保留。";
        }
        finally
        {
            _phase = TrafficPhase.Idle;
            if (_automaticMonitor) await StopMonitorAsync();
            _automaticMonitor = false;
            _speedCts.Dispose();
            _speedCts = null;
            if (canceled)
            {
                if (!completed.Any(r => r.Phase == TrafficPhase.Download)) { DownloadValue = "—"; DownloadHint = "已取消 · 无完整结果"; }
                if (options.UploadUri != null && !completed.Any(r => r.Phase == TrafficPhase.Upload)) { UploadValue = "—"; UploadHint = "已取消 · 无完整结果"; }
            }
            if (!_closing) RefreshControls();
        }
    }

    private string BuildSummary(int firstSample)
    {
        double? Average(TrafficPhase phase)
        {
            var values = _samples.Skip(firstSample).Where(s => s.Phase == phase && s.Success).Select(s => s.LatencyMs!.Value).ToArray();
            return values.Length == 0 ? null : values.Average();
        }
        var idle = Average(TrafficPhase.Idle);
        var down = Average(TrafficPhase.Download);
        var up = Average(TrafficPhase.Upload);
        string F(double? n) => n is { } value ? $"{value:F1}" : "—";
        return $"平均延迟：空闲 {F(idle)} / 下载 {F(down)} / 上传 {F(up)} ms。结果为到当前节点的有效吞吐。";
    }

    private void ResetSamples()
    {
        _samples.Clear(); RecentSamples.Clear(); _stats = new();
        LatencyValue = JitterValue = "—";
        LatencyPlot.SetPoints([]);
        _session.Reset(); ElapsedText = "00:00:00";
        Notify(nameof(AverageHint), nameof(LossHint), nameof(RangeHint), nameof(SampleCountText));
        RefreshControls();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        ResetSamples(); _speedPoints.Clear(); SpeedPlot.SetPoints([]);
        DownloadValue = UploadValue = "—"; DownloadHint = UploadHint = "尚未测速";
        _downloadBytes = _uploadBytes = 0; Notify(nameof(TrafficText));
        ProgressValue = 0; StatusText = "准备就绪"; StatusDetail = "记录已清空，可以开始新的测试。";
    }

    private void Gateway_Click(object sender, RoutedEventArgs e)
    {
        try { TargetBox.Text = ProbeService.FindGateway() ?? throw new InvalidOperationException("没有找到可用的 IPv4 网关，请手动输入目标。"); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV 表格 (*.csv)|*.csv", FileName = $"NetPulse-{DateTime.Now:yyyyMMdd-HHmmss}.csv", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, CsvExporter.Export(_samples), new UTF8Encoding(true));
            MessageBox.Show(this, $"已导出 {_samples.Count:N0} 条记录。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowError(Exception ex)
    { StatusText = "操作未完成"; StatusDetail = ex.GetBaseException().Message; }

}
