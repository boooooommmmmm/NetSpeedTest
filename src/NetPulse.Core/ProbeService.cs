using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetPulse.Core;

public static class ProbeService
{
    public static string ValidateHost(string host)
    {
        host = host.Trim().Trim('[', ']');
        if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException("请输入有效的 IP 地址或域名，不包含 http:// 和路径。");
        return host;
    }

    public static async Task<ProbeSample> ProbeAsync(string host, ProbeMode mode, int port,
        int timeoutMs, TrafficPhase phase, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            if (mode == ProbeMode.Icmp)
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(timeoutMs),
                    new byte[32], null, cancellationToken).ConfigureAwait(false);
                return new(DateTimeOffset.Now, host, mode, phase,
                    reply.Status == IPStatus.Success ? reply.RoundtripTime : null,
                    reply.Status == IPStatus.Success ? reply.Address.ToString() : reply.Status.ToString());
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMs);
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return new(DateTimeOffset.Now, host, mode, phase, watch.Elapsed.TotalMilliseconds, $"TCP :{port}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { return new(DateTimeOffset.Now, host, mode, phase, null, "连接超时"); }
        catch (Exception e) when (e is PingException or SocketException or ArgumentException or InvalidOperationException)
        { return new(DateTimeOffset.Now, host, mode, phase, null, e.GetBaseException().Message); }
    }

    public static string? FindGateway() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().GatewayAddresses)
        .Select(g => g.Address)
        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))?.ToString();
}
