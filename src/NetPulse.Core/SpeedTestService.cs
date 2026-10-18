using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace NetPulse.Core;

public enum SpeedProtocol { BytesQuery, SpeedTestCn }

public sealed record SpeedOptions(Uri DownloadUri, Uri? UploadUri, int DurationSeconds = 10,
    int Connections = 4, long MaxBytesPerDirection = 256L * 1024 * 1024, bool UseProxy = false,
    SpeedProtocol Protocol = SpeedProtocol.BytesQuery)
{
    public void Validate()
    {
        ValidateUri(DownloadUri);
        if (UploadUri != null) ValidateUri(UploadUri);
        if (DurationSeconds is < 1 or > 120 || Connections is < 1 or > 16 || MaxBytesPerDirection < 1024)
            throw new ArgumentException("测速时长、连接数或流量上限无效。");
    }

    public static Uri ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != "https" && uri.Scheme != "http") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("测速地址必须为有效的 HTTP / HTTPS 地址，且不能包含用户名或密码。");
        return uri;
    }
}

public sealed record SpeedProgress(TrafficPhase Phase, double MegabitsPerSecond, long Bytes,
    double ElapsedSeconds, double Fraction);
public sealed record SpeedResult(TrafficPhase Phase, double MegabitsPerSecond, long Bytes,
    double ElapsedSeconds, bool ReachedLimit, IReadOnlyList<string> Errors);

public sealed class SpeedTestService
{
    public async Task<SpeedResult> RunPhaseAsync(SpeedOptions options, TrafficPhase phase,
        IProgress<SpeedProgress>? progress, CancellationToken cancellationToken)
    {
        options.Validate();
        if (phase == TrafficPhase.Idle || (phase == TrafficPhase.Upload && options.UploadUri == null))
            throw new ArgumentException("未配置此测速项目。");
        using var handler = new SocketsHttpHandler
        {
            UseProxy = options.UseProxy,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = options.Connections,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(8, options.DurationSeconds)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NetPulse/1.2");
        http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.DurationSeconds));
        using var reporting = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        long bytes = 0, reserved = 0;
        var errors = new ConcurrentQueue<string>();
        // A shared incompressible payload prevents request compression from inflating upload results.
        byte[] payload = phase == TrafficPhase.Upload ? RandomNumberGenerator.GetBytes(1024 * 1024) : [];
        var reportTask = ReportAsync();
        var workers = Enumerable.Range(0, options.Connections).Select(_ => WorkerAsync()).ToArray();
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        finally { watch.Stop(); reporting.Cancel(); await reportTask.ConfigureAwait(false); }
        cancellationToken.ThrowIfCancellationRequested();
        var total = Interlocked.Read(ref bytes);
        if (total == 0)
            throw new HttpRequestException(errors.TryPeek(out var error) ? error : "在限定时间内没有收到有效数据。请更换节点，或增加测速时长。");
        return new(phase, total * 8d / Math.Max(.001, watch.Elapsed.TotalSeconds) / 1_000_000,
            total, watch.Elapsed.TotalSeconds, total >= options.MaxBytesPerDirection, errors.Distinct().ToArray());

        long Reserve(int chunk)
        {
            while (true)
            {
                var current = Interlocked.Read(ref reserved);
                var amount = Math.Min(chunk, options.MaxBytesPerDirection - current);
                if (amount <= 0) return 0;
                if (Interlocked.CompareExchange(ref reserved, current + amount, current) == current) return amount;
            }
        }

        async Task WorkerAsync()
        {
            var buffer = new byte[64 * 1024];
            while (!deadline.IsCancellationRequested)
            {
                var amount = Reserve(phase == TrafficPhase.Download
                    ? (options.Protocol == SpeedProtocol.SpeedTestCn ? 16 * 1024 * 1024 : 4 * 1024 * 1024)
                    : payload.Length);
                if (amount == 0) break;
                try
                {
                    var endpoint = phase == TrafficPhase.Download ? options.DownloadUri : options.UploadUri!;
                    var builder = new UriBuilder(endpoint);
                    var query = builder.Query.TrimStart('?');
                    var sizeKey = options.Protocol == SpeedProtocol.SpeedTestCn ? "size" : "bytes";
                    var fields = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                        .Where(q => !q.Split('=')[0].Equals(sizeKey, StringComparison.OrdinalIgnoreCase));
                    builder.Query = string.Join('&', fields.Append($"{sizeKey}={amount}").Append($"netpulse={Guid.NewGuid():N}"));
                    using var request = new HttpRequestMessage(phase == TrafficPhase.Download ? HttpMethod.Get : HttpMethod.Post, builder.Uri);
                    request.Headers.AcceptEncoding.ParseAdd("identity");
                    if (phase == TrafficPhase.Upload)
                    {
                        request.Content = new ByteArrayContent(payload, 0, (int)amount);
                        request.Content.Headers.ContentType = new("application/octet-stream");
                    }
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    if (phase == TrafficPhase.Upload)
                    {
                        Interlocked.Add(ref bytes, amount); // Count only POSTs acknowledged with a successful response.
                        continue;
                    }
                    var type = response.Content.Headers.ContentType?.MediaType;
                    if (type?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true ||
                        type?.Contains("json", StringComparison.OrdinalIgnoreCase) == true || response.Content.Headers.ContentEncoding.Count > 0)
                        throw new HttpRequestException("下载节点返回网页、文本或压缩内容，请填写直接下载二进制数据的地址。");
                    await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                    long received = 0;
                    while (received < amount)
                    {
                        var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, amount - received)), deadline.Token).ConfigureAwait(false);
                        if (count == 0) break;
                        received += count;
                        Interlocked.Add(ref bytes, count);
                    }
                    if (received == 0) throw new HttpRequestException("下载节点返回了空内容。");
                    Interlocked.Add(ref reserved, -(amount - received));
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { break; }
                catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
                { errors.Enqueue(e.GetBaseException().Message); break; }
            }
        }

        async Task ReportAsync()
        {
            long lastBytes = 0;
            double lastTime = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
            try
            {
                while (await timer.WaitForNextTickAsync(reporting.Token).ConfigureAwait(false))
                {
                    var now = watch.Elapsed.TotalSeconds;
                    var current = Interlocked.Read(ref bytes);
                    var speed = (current - lastBytes) * 8d / Math.Max(.001, now - lastTime) / 1_000_000;
                    progress?.Report(new(phase, speed, current, now,
                        Math.Clamp(Math.Max(now / options.DurationSeconds, current / (double)options.MaxBytesPerDirection), 0, 1)));
                    lastBytes = current;
                    lastTime = now;
                }
            }
            catch (OperationCanceledException) when (reporting.IsCancellationRequested) { }
        }
    }
}
