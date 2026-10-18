using System.Net;
using System.Text.Json;

namespace NetPulse.Core;

public enum NodeCatalogProvider { SpeedTestCn, SpeedTestNet }

public sealed record NodeSyncSettings(bool Enabled = true, int IntervalMinutes = 15, bool UseProxy = false,
    NodeCatalogProvider Provider = NodeCatalogProvider.SpeedTestNet)
{
    public bool IsValid => IntervalMinutes is 5 or 15 or 30 or 60 && Enum.IsDefined(Provider);
}

public sealed class NodeSyncSchedule
{
    public NodeSyncSettings Settings { get; private set; }
    public DateTimeOffset? LastSuccess { get; private set; }
    public DateTimeOffset? NextAttempt { get; private set; }

    public NodeSyncSchedule(NodeSyncSettings settings, DateTimeOffset? lastSuccess = null)
    {
        Settings = settings.IsValid ? settings : new();
        LastSuccess = lastSuccess;
        NextAttempt = NextAfter(lastSuccess);
    }

    public void Configure(NodeSyncSettings settings)
    {
        if (!settings.IsValid) throw new ArgumentException("同步间隔必须是 5、15、30 或 60 分钟。");
        Settings = settings;
        NextAttempt = NextAfter(LastSuccess);
    }

    public bool IsDue(DateTimeOffset now) => Settings.Enabled && (NextAttempt == null || now >= NextAttempt);
    public bool CanRun(DateTimeOffset now, bool busy) => !busy && IsDue(now);
    public void RequestNow() => NextAttempt = null;
    public void Succeeded(DateTimeOffset now) { LastSuccess = now; NextAttempt = NextAfter(now); }
    public void Failed(DateTimeOffset now) => NextAttempt = Settings.Enabled ? now.AddMinutes(1) : null;
    private DateTimeOffset? NextAfter(DateTimeOffset? success) => !Settings.Enabled || success == null ? null : success.Value.AddMinutes(Settings.IntervalMinutes);
}

public sealed record NodeCatalogSnapshot(DateTimeOffset SyncedAt, bool UseProxy, IReadOnlyList<RankedSpeedNode> Nodes, string? SelectedNodeId = null, int Version = 1,
    NodeCatalogProvider Provider = NodeCatalogProvider.SpeedTestCn)
{
    public bool IsValid => Version == 1 && Enum.IsDefined(Provider) && Nodes is { Count: > 0 and <= 30 } && Nodes.All(n => n != null && n.Node != null &&
        IsHttp(n.Node.DownloadUri) && IsHttp(n.Node.UploadUri) && IsHttp(n.Node.PingUri) &&
        n.Node.Protocol == (Provider == NodeCatalogProvider.SpeedTestNet ? SpeedProtocol.SpeedTestNet : SpeedProtocol.SpeedTestCn) &&
        (n.IpAddresses == null || n.IpAddresses.All(ip => IPAddress.TryParse(ip, out _))));
    private static bool IsHttp(Uri? uri) => uri is { IsAbsoluteUri: true } && (uri.Scheme is "https" or "http") && string.IsNullOrEmpty(uri.UserInfo);
}

public sealed class NodeCatalogStorage(string directory, NodeCatalogProvider provider = NodeCatalogProvider.SpeedTestCn)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public NodeCatalogProvider Provider { get; } = provider;
    private string CatalogFile => Provider == NodeCatalogProvider.SpeedTestNet ? "nodes-speedtest-net-v1.json" : "nodes-v1.json";
    public NodeSyncSettings LoadSettings()
    {
        var settings = Read<NodeSyncSettings>("settings.json");
        return settings is { IsValid: true } ? settings : new();
    }
    public NodeCatalogSnapshot? LoadCatalog()
    {
        var catalog = Read<NodeCatalogSnapshot>(CatalogFile);
        return catalog is { IsValid: true } && catalog.Provider == Provider ? catalog : null;
    }
    public void SaveSettings(NodeSyncSettings settings)
    {
        if (!settings.IsValid) throw new ArgumentException("无效的同步设置。");
        Write("settings.json", settings);
    }
    public void SaveCatalog(NodeCatalogSnapshot catalog)
    {
        if (!catalog.IsValid || catalog.Provider != Provider) throw new ArgumentException("无效的节点缓存或节点源不匹配。");
        Write(CatalogFile, catalog);
    }
    private T? Read<T>(string name)
    {
        try
        {
            var path = Path.Combine(DirectoryPath, name);
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return default; }
    }
    private void Write<T>(string name, T value)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, name);
        var temporary = Path.Combine(DirectoryPath, name + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class NodeIpResolver
{
    public static async Task<IReadOnlyList<RankedSpeedNode>> ResolveAsync(IEnumerable<RankedSpeedNode> nodes, CancellationToken token)
    {
        using var slots = new SemaphoreSlim(4);
        var resolved = await Task.WhenAll(nodes.Select(async node =>
        {
            await slots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var addresses = await Dns.GetHostAddressesAsync(node.Node.DownloadUri.DnsSafeHost, timeout.Token).ConfigureAwait(false);
                return node with { IpAddresses = addresses.Select(a => a.ToString()).Distinct().Order().ToArray() };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException)
            { return node with { IpAddresses = [] }; }
            finally { slots.Release(); }
        })).ConfigureAwait(false);
        return resolved;
    }
}
