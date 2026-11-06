using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using NetPulse.Core;

namespace NetPulse;

public partial class MainWindow
{
    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (IsNodeSource) SwitchCatalog(SourceBox.SelectedIndex == 3 ? NodeCatalogProvider.SpeedTestNet : NodeCatalogProvider.SpeedTestCn);
        ResetSpeedReadings(); UpdateSourceUi();
    }

    private void SwitchCatalog(NodeCatalogProvider provider)
    {
        if (_nodeStorage.Provider == provider) return;
        var settings = _syncSchedule.Settings with { Provider = provider };
        _nodeStorage = new(_nodeStorage.DirectoryPath, provider);
        _nodeSnapshot = _nodeStorage.LoadCatalog();
        _syncSchedule = new(settings, _nodeSnapshot?.SyncedAt);
        if (_nodeSnapshot != null && _nodeSnapshot.UseProxy != settings.UseProxy) _syncSchedule.RequestNow();
        _syncMessage = null;
        _applyingCatalog = true;
        try { NodeBox.ItemsSource = null; }
        finally { _applyingCatalog = false; }
        SyncedIpRows.Clear();
        _nodesUseProxy = settings.UseProxy;
        if (_nodeSnapshot != null) ApplyCatalog(_nodeSnapshot);
        try { _nodeStorage.SaveSettings(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _syncMessage = "节点源设置未能保存：" + ex.Message; }
        Notify(nameof(NodePickerLabel), nameof(AutoSyncLabel), nameof(SyncedNodesHeader), nameof(SyncStatus));
    }

    private void UpdateSourceUi()
    {
        if (IsNodeSource)
        {
            DownloadUrlBox.Text = UploadUrlBox.Text = "";
            NodeStatus = $"自动同步 {CatalogSourceName} 节点；也可点击立即同步后手动选择。";
            ApplySelectedNode();
        }
        else if (SourceBox.SelectedIndex == 1)
        {
            DownloadUrlBox.Text = "https://speed.cloudflare.com/__down";
            UploadUrlBox.Text = "https://speed.cloudflare.com/__up";
            NodeStatus = "Cloudflare 国际对照节点；跨境链路可能限制结果。监测目标与测速节点分别设置。";
        }
        else NodeStatus = "在下方“测速设置与自定义节点”中填写 URL；下载使用 bytes 参数，上传接受原始二进制 POST。";
        Notify(nameof(NodePickerVisibility), nameof(EndpointsReadOnly));
    }

    private void Node_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_applyingCatalog && IsNodeSource)
        {
            ResetSpeedReadings(); ApplySelectedNode();
            if (_nodeSnapshot != null && NodeBox.SelectedItem is RankedSpeedNode selected)
            {
                _nodeSnapshot = _nodeSnapshot with { SelectedNodeId = selected.Node.Id };
                SaveCatalog();
            }
        }
    }

    private void ResetSpeedReadings()
    {
        DownloadValue = UploadValue = "—";
        DownloadHint = UploadHint = "尚未测速";
        _downloadBytes = _uploadBytes = 0;
        _speedPoints.Clear(); SpeedPlot.SetPoints([]);
        ProgressValue = 0;
        Notify(nameof(TrafficText));
        if (_speedCts == null)
        {
            StatusText = _monitorCts == null ? "准备就绪" : "正在监测";
            StatusDetail = "测速源或节点已切换，可以开始新的测速。";
        }
    }

    private void ApplySelectedNode()
    {
        if (NodeBox.SelectedItem is not RankedSpeedNode selected) return;
        DownloadUrlBox.Text = selected.Node.DownloadUri.AbsoluteUri;
        UploadUrlBox.Text = selected.Node.UploadUri.AbsoluteUri;
        NodeStatus = "当前节点：" + selected.DisplayName + " · IP " + selected.IpText;
    }

    private void SyncSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var settings = new NodeSyncSettings(AutoSyncBox.IsChecked == true, Selected(SyncIntervalBox), ProxyBox.IsChecked == true, _nodeStorage.Provider);
        var proxyChanged = settings.UseProxy != _syncSchedule.Settings.UseProxy;
        _syncSchedule.Configure(settings);
        if (proxyChanged) _syncSchedule.RequestNow();
        _syncMessage = null;
        try { _nodeStorage.SaveSettings(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _syncMessage = "设置未能保存：" + ex.Message; }
        Notify(nameof(SyncStatus));
    }

    private async Task CheckAutoSyncAsync()
    {
        Notify(nameof(SyncStatus));
        if (_closing || !_syncSchedule.CanRun(DateTimeOffset.UtcNow, _speedCts != null || _loadingNodes || _updateDownloadCts != null)) return;
        try { await LoadNodesAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* LoadNodesAsync retains the cache and presents the next retry time. */ }
    }

    private void ApplyCatalog(NodeCatalogSnapshot snapshot)
    {
        var selectedId = (NodeBox.SelectedItem as RankedSpeedNode)?.Node.Id ?? snapshot.SelectedNodeId;
        _applyingCatalog = true;
        try
        {
            NodeBox.ItemsSource = snapshot.Nodes;
            NodeBox.SelectedItem = snapshot.Nodes.FirstOrDefault(n => n.Node.Id == selectedId)
                ?? snapshot.Nodes.FirstOrDefault(n => n.LatencyMs.HasValue) ?? snapshot.Nodes[0];
        }
        finally { _applyingCatalog = false; }
        _nodeSnapshot = snapshot with { SelectedNodeId = (NodeBox.SelectedItem as RankedSpeedNode)?.Node.Id };
        _nodesUseProxy = snapshot.UseProxy;
        SyncedIpRows.Clear();
        foreach (var node in snapshot.Nodes)
            SyncedIpRows.Add(new(node.Node.DisplayName, node.Node.DownloadUri.DnsSafeHost, node.IpText,
                node.LatencyMs is { } latency ? $"{latency:F0} ms" : "不可达"));
        if (IsNodeSource) ApplySelectedNode();
        Notify(nameof(SyncedNodesHeader), nameof(SyncStatus));
    }

    private void SaveCatalog()
    {
        if (_nodeSnapshot == null) return;
        try { _nodeStorage.SaveCatalog(_nodeSnapshot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _syncMessage = "节点已更新，但本地缓存未能保存：" + ex.Message; Notify(nameof(SyncStatus)); }
    }

    private async void RefreshNodes_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadNodesAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private async Task LoadNodesAsync()
    {
        if (_nodeCts != null || _speedCts != null || _updateDownloadCts != null || _closing) return;
        _nodeCts = new();
        var token = _nodeCts.Token;
        _loadingNodes = true;
        var useProxy = ProxyBox.IsChecked == true;
        _syncMessage = null;
        Notify(nameof(SyncStatus));
        RefreshControls();
        try
        {
            var client = new SpeedTestCnClient();
            var nodes = await _fetchNodeCatalog(useProxy, token);
            var ranked = await client.RankNodesAsync(nodes, useProxy, token);
            var resolved = await NodeIpResolver.ResolveAsync(ranked, token);
            token.ThrowIfCancellationRequested();
            if (resolved.Count == 0) throw new HttpRequestException($"{CatalogSourceName} 未返回有效节点，已保留上次缓存。");
            var completedAt = DateTimeOffset.UtcNow;
            ApplyCatalog(new(completedAt, useProxy, resolved, Provider: _nodeStorage.Provider));
            _syncSchedule.Succeeded(completedAt);
            SaveCatalog();
        }
        catch (OperationCanceledException)
        {
            _syncSchedule.Failed(DateTimeOffset.UtcNow);
            _syncMessage = "同步已取消，保留已有节点" + (_syncSchedule.Settings.Enabled ? "；1 分钟后重试" : "");
            throw;
        }
        catch (Exception ex)
        {
            _syncSchedule.Failed(DateTimeOffset.UtcNow);
            _syncMessage = "同步失败，保留已有节点" + (_syncSchedule.Settings.Enabled ? "；1 分钟后重试" : "") + "：" + ex.GetBaseException().Message;
            throw;
        }
        finally
        {
            _loadingNodes = false;
            _nodeCts.Dispose();
            _nodeCts = null;
            Notify(nameof(SyncStatus));
            if (!_closing) RefreshControls();
        }
    }
}
