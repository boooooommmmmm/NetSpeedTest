using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace NetPulse.Core;

/// <summary>Public Speedtest.net catalog; HTTP transfers use the advertised HTTPS server.</summary>
public sealed class SpeedTestNetClient
{
    private const int MaxCatalogBytes = 1024 * 1024;
    private static readonly string[] CatalogUrls =
    [
        "https://www.speedtest.net/api/js/servers?engine=js&limit=30&https_functional=true",
        "https://www.speedtest.net/speedtest-servers-static.php"
    ];

    public async Task<IReadOnlyList<SpeedTestNode>> GetNodesAsync(bool useProxy, CancellationToken token)
    {
        using var http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = useProxy, ConnectTimeout = TimeSpan.FromSeconds(3),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NetPulse/1.4");
        http.DefaultRequestHeaders.CacheControl = new() { NoCache = true, NoStore = true };
        var errors = new List<string>();
        foreach (var url in CatalogUrls)
        {
            token.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
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
                    if (body.Length + count > MaxCatalogBytes) throw new HttpRequestException("节点列表超过允许大小。");
                    body.Write(buffer, 0, count);
                }
                var nodes = ParseNodes(System.Text.Encoding.UTF8.GetString(body.ToArray()));
                if (nodes.Count == 0) throw new HttpRequestException("未返回可用的 HTTPS 测速节点。");
                return nodes;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            { errors.Add(ex is OperationCanceledException ? "请求超时" : ex.GetBaseException().Message); }
        }
        throw new HttpRequestException("获取 Speedtest.net 节点失败，请立即同步或切换测速源：" + string.Join("；", errors.Distinct()));
    }

    public static IReadOnlyList<SpeedTestNode> ParseNodes(string response)
    {
        if (response.Length > MaxCatalogBytes) throw new HttpRequestException("Speedtest.net 节点列表超过允许大小。");
        try
        {
            var records = new List<Dictionary<string, string>>();
            if (response.TrimStart().StartsWith('<'))
            {
                using var reader = XmlReader.Create(new StringReader(response), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxCatalogBytes });
                var document = XDocument.Load(reader);
                records.AddRange(document.Descendants("server").Take(500)
                    .Select(e => e.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value)));
            }
            else
            {
                using var document = JsonDocument.Parse(response);
                if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected a server array.");
                foreach (var item in document.RootElement.EnumerateArray().Take(500))
                    if (item.ValueKind == JsonValueKind.Object)
                        records.Add(item.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString()));
            }
            var nodes = new List<SpeedTestNode>();
            foreach (var item in records)
            {
                string Text(string key) => item.GetValueOrDefault(key, "").Trim();
                var id = Text("id");
                if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0 || nodes.Any(n => n.Id == id)) continue;
                if (Text("https_functional") == "0" || Text("https_functional").Equals("false", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Uri.TryCreate(Text("url"), UriKind.Absolute, out var upload) || upload.Scheme is not ("http" or "https") ||
                    !string.IsNullOrEmpty(upload.UserInfo) || !upload.AbsolutePath.EndsWith("/upload.php", StringComparison.OrdinalIgnoreCase)) continue;
                var builder = new UriBuilder(upload) { Scheme = "https", Query = "", Fragment = "" };
                // The catalog's host may be the certificate-bearing *.ooklaserver.net alias.
                var host = Text("host");
                if (host.Length > 0)
                {
                    if (!Uri.TryCreate("https://" + host, UriKind.Absolute, out var advertised) || !string.IsNullOrEmpty(advertised.UserInfo) ||
                        advertised.AbsolutePath != "/" || advertised.Query.Length > 0 || advertised.Fragment.Length > 0) continue;
                    builder.Host = advertised.Host;
                    builder.Port = advertised.Port;
                }
                else if (upload.IsDefaultPort) builder.Port = 443;
                var secureUpload = builder.Uri;
                var distance = double.TryParse(Text("distance"), CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0 ? value : double.MaxValue;
                nodes.Add(new(id, Text("sponsor"), Text("country"), Text("name"), "",
                    new(secureUpload, "random2000x2000.jpg"), secureUpload, new(secureUpload, "latency.txt"), distance, SpeedProtocol.SpeedTestNet));
            }
            return nodes.OrderBy(n => n.Distance).Take(30).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or XmlException or ArgumentException or UriFormatException)
        { throw new HttpRequestException("Speedtest.net 公开节点接口格式已变化，请切换其他测速源。", ex); }
    }
}
