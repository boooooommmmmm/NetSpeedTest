using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetPulse.Core;

public sealed record SpeedTestNode(string Id, string Name, string Province, string City, string Operator,
    Uri DownloadUri, Uri UploadUri, Uri PingUri, double Distance, SpeedProtocol Protocol = SpeedProtocol.SpeedTestCn)
{
    public string DisplayName => string.Join(" · ", new[] { string.Join(" ", new[] { Province, City }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct()), Operator, Name }
        .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
    public override string ToString() => DisplayName;
}

public sealed record RankedSpeedNode(SpeedTestNode Node, double? LatencyMs, IReadOnlyList<string>? IpAddresses = null)
{
    public string DisplayName => Node.DisplayName + (LatencyMs is { } ms ? $"  ({ms:F0} ms)" : "  (暂不可达)");
    public string IpText => IpAddresses is { Count: > 0 } ? string.Join(" / ", IpAddresses) : "未解析";
    public override string ToString() => DisplayName;
}

/// <summary>Adapter for the public node catalog used by https://www.speedtest.cn/.</summary>
public sealed class SpeedTestCnClient
{
    // Public wire-format constants published in the site's browser JavaScript, not account credentials.
    private static readonly byte[] CatalogKey = Encoding.UTF8.GetBytes("5ECC5D62140EC099");
    private static readonly byte[] CatalogIv = Encoding.UTF8.GetBytes("E63EA892A702EEAA");
    private const int MaxCatalogBytes = 1024 * 1024;

    public async Task<IReadOnlyList<SpeedTestNode>> GetNodesAsync(bool useProxy, CancellationToken token)
    {
        using var http = CreateClient(useProxy);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var parameters = JsonSerializer.Serialize(new { type = "multi", https = 1, browser = 1, domainType = 2, use_cdn = 0 });
        using var aes = CreateCipher();
        var encoded = Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(parameters), CatalogIv, PaddingMode.PKCS7));
        var url = "https://nodes-api.speedtest.cn/?data=" + Uri.EscapeDataString(encoded) + "&netpulse=" + Guid.NewGuid().ToString("N");
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (body.Length + count > MaxCatalogBytes) throw new HttpRequestException("测速网节点列表超过允许大小。");
                body.Write(buffer, 0, count);
            }
            var nodes = ParseNodes(Encoding.UTF8.GetString(body.ToArray()));
            if (nodes.Count == 0) throw new HttpRequestException("测速网未返回可直接使用的公开节点，请刷新或切换测速源。");
            return nodes;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new HttpRequestException("获取测速网节点超时，请立即同步或切换测速源。"); }
    }

    public async Task<IReadOnlyList<RankedSpeedNode>> RankNodesAsync(IEnumerable<SpeedTestNode> nodes,
        bool useProxy, CancellationToken token)
    {
        using var http = CreateClient(useProxy);
        using var slots = new SemaphoreSlim(4);
        var tasks = nodes.Take(12).Select(async node =>
        {
            await slots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(2500));
                var watch = Stopwatch.StartNew();
                using var response = await http.GetAsync(node.PingUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var healthy = response.IsSuccessStatusCode;
                if (healthy && node.Protocol == SpeedProtocol.SpeedTestNet)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    var buffer = new byte[32];
                    var count = await stream.ReadAtLeastAsync(buffer, 10, throwOnEndOfStream: false, timeout.Token).ConfigureAwait(false);
                    healthy = Encoding.ASCII.GetString(buffer, 0, count).Trim() == "test=test";
                }
                return new RankedSpeedNode(node, healthy ? watch.Elapsed.TotalMilliseconds : null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
            { return new RankedSpeedNode(node, null); }
            finally { slots.Release(); }
        });
        return (await Task.WhenAll(tasks).ConfigureAwait(false))
            .OrderBy(n => n.LatencyMs ?? double.MaxValue).ThenBy(n => n.Node.Distance).ToArray();
    }

    public static IReadOnlyList<SpeedTestNode> ParseNodes(string response)
    {
        try
        {
            using var wrapper = JsonDocument.Parse(response);
            var root = wrapper.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var encrypted) && encrypted.ValueKind == JsonValueKind.String)
            {
                using var aes = CreateCipher();
                var bytes = aes.DecryptCbc(Convert.FromBase64String(encrypted.GetString()!), CatalogIv, PaddingMode.PKCS7);
                using var decoded = JsonDocument.Parse(bytes);
                return ParseRoot(decoded.RootElement);
            }
            return ParseRoot(root);
        }
        catch (Exception e) when (e is JsonException or CryptographicException or FormatException)
        { throw new HttpRequestException("测速网公开节点接口的格式已变化，请切换其他测速源。", e); }
    }

    private static IReadOnlyList<SpeedTestNode> ParseRoot(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)) root = data;
        if (root.ValueKind != JsonValueKind.Array) throw new HttpRequestException("测速网未返回有效节点列表。");
        var result = new List<SpeedTestNode>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            // Skip restricted/private nodes and CDN URLs whose signatures may require a different protocol.
            if (Text(item, "isPrivate") is "1" or "true" || Text(item, "active") == "2" || Text(item, "is_cdn") is "1" or "true") continue;
            if (!TryUri(item, "downloadUrl", out var down) || !TryUri(item, "uploadUrl", out var up) || !TryUri(item, "pingUrl", out var ping)) continue;
            var id = Text(item, "id");
            if (id.Length == 0 || result.Any(n => n.Id == id)) continue;
            var distance = double.TryParse(Text(item, "distance"), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.MaxValue;
            result.Add(new(id, Text(item, "sponsor"), Text(item, "province"), Text(item, "city"), Text(item, "operator"), down!, up!, ping!, distance));
        }
        return result.OrderBy(n => n.Distance).Take(30).ToArray();
    }

    private static string Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) ? value.ToString() : "";
    private static bool TryUri(JsonElement item, string key, out Uri? uri) => Uri.TryCreate(Text(item, key), UriKind.Absolute, out uri) &&
        (uri.Scheme == "https" || uri.Scheme == "http") && string.IsNullOrEmpty(uri.UserInfo);
    private static Aes CreateCipher()
    { var aes = Aes.Create(); aes.Key = CatalogKey; return aes; }
    private static HttpClient CreateClient(bool useProxy)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = useProxy,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NetPulse/1.4");
        client.DefaultRequestHeaders.CacheControl = new() { NoCache = true, NoStore = true };
        return client;
    }
}
