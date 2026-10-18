using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using NetPulse.Core;

var passed = 0;
var failed = 0;
await using var server = new LocalServer();
await Run("Latency statistics: loss, percentile, and jitter across a lost probe", () =>
{
    var stats = new LatencyStatistics();
    foreach (double? latency in new double?[] { 10, 20, null, 50, 80 })
        stats.Add(new(DateTimeOffset.Now, "localhost", ProbeMode.Icmp, TrafficPhase.Idle, latency, ""));
    Check(stats.Total == 5 && stats.Received == 4 && stats.LossPercent == 20, "Loss incorrect");
    Check(stats.Average == 40 && stats.Jitter == 20 && stats.P95 == 80 && stats.Minimum == 10 && stats.Maximum == 80, "Distribution incorrect");
    return Task.CompletedTask;
});
await Run("CSV: invariant numbers, escaping, and spreadsheet formula protection", () =>
{
    var csv = CsvExporter.Export([new(DateTimeOffset.Now, "=bad,\"host", ProbeMode.Tcp, TrafficPhase.Upload, 12.34, "+formula\nline")]);
    Check(csv.Contains("12.340") && csv.Contains("\"'=bad,\"\"host\"") && csv.Contains("\"'+formula\nline\""), "CSV encoding incorrect");
    return Task.CompletedTask;
});
await Run("Input validation rejects URLs as hosts and non-HTTP speed endpoints", () =>
{
    Throws<ArgumentException>(() => ProbeService.ValidateHost("https://example.com/path"));
    Throws<ArgumentException>(() => SpeedOptions.ValidateUri(new("file:///C:/test")));
    Throws<ArgumentException>(() => new SpeedOptions(server.Uri("down"), null, 0).Validate());
    Check(ProbeService.ValidateHost("[::1]") == "::1", "IPv6 normalization failed");
    return Task.CompletedTask;
});
await Run("Real ICMP probe against loopback", async () =>
{
    var probe = await ProbeService.ProbeAsync("127.0.0.1", ProbeMode.Icmp, 443, 1000, TrafficPhase.Idle, default);
    Check(probe.Success, probe.Detail);
});
await Run("TCP success and closed-port failure", async () =>
{
    var ok = await ProbeService.ProbeAsync("127.0.0.1", ProbeMode.Tcp, server.Port, 1000, TrafficPhase.Download, default);
    Check(ok.Success && ok.Phase == TrafficPhase.Download, "TCP success failed");
    var unused = new TcpListener(IPAddress.Loopback, 0); unused.Start();
    var port = ((IPEndPoint)unused.LocalEndpoint).Port; unused.Stop();
    var fail = await ProbeService.ProbeAsync("127.0.0.1", ProbeMode.Tcp, port, 1000, TrafficPhase.Idle, default);
    Check(!fail.Success, "Closed port should fail");
});
await Run("Download uses real bytes, respects cap across concurrent connections", async () =>
{
    const long cap = 2 * 1024 * 1024 + 17;
    var result = await new SpeedTestService().RunPhaseAsync(new(server.Uri("down?bytes=1"), null, 3, 4, cap), TrafficPhase.Download, null, default);
    Check(result.Bytes == cap && result.ReachedLimit && result.MegabitsPerSecond > 0 && result.Errors.Count == 0, "Download count incorrect");
});
await Run("SpeedTest.cn download uses size instead of bytes and respects the cap", async () =>
{
    const long cap = 2 * 1024 * 1024 + 19;
    var result = await new SpeedTestService().RunPhaseAsync(new(server.Uri("cn-down"), null, 3, 4, cap, Protocol: SpeedProtocol.SpeedTestCn), TrafficPhase.Download, null, default);
    Check(result.Bytes == cap && result.ReachedLimit && result.Errors.Count == 0, "SpeedTest.cn protocol mapping failed");
});
await Run("SpeedTest.cn catalog: public nodes only, duplicate and invalid URL filtering", () =>
{
    var data = """
    {"data":[
      {"id":"1","sponsor":"示例节点","province":"江苏","city":"南京","operator":"广电","downloadUrl":"https://example.com/download","uploadUrl":"https://example.com/upload","pingUrl":"https://example.com/hello","distance":10,"isPrivate":0,"active":"1","is_cdn":"0"},
      {"id":"2","downloadUrl":"https://example.com/download","uploadUrl":"https://example.com/upload","pingUrl":"https://example.com/hello","isPrivate":1},
      {"id":"3","downloadUrl":"https://example.com/download","uploadUrl":"https://example.com/upload","pingUrl":"https://example.com/hello","is_cdn":"1"},
      {"id":"4","downloadUrl":"file:///C:/test","uploadUrl":"https://example.com/upload","pingUrl":"https://example.com/hello"},
      {"id":"1","downloadUrl":"https://example.com/duplicate","uploadUrl":"https://example.com/upload","pingUrl":"https://example.com/hello"}
    ]}
    """;
    var parsed = SpeedTestCnClient.ParseNodes(data);
    Check(parsed.Count == 1 && parsed[0].Id == "1" && parsed[0].DisplayName.Contains("南京"), "Catalog filtering failed");
    using var aes = Aes.Create(); aes.Key = Encoding.UTF8.GetBytes("5ECC5D62140EC099");
    var encoded = aes.EncryptCbc(Encoding.UTF8.GetBytes(data), Encoding.UTF8.GetBytes("E63EA892A702EEAA"), PaddingMode.PKCS7);
    var wrapper = JsonSerializer.Serialize(new { data = Convert.ToBase64String(encoded) });
    Check(SpeedTestCnClient.ParseNodes(wrapper).Single().Id == "1", "Public catalog wire format failed");
    Throws<HttpRequestException>(() => SpeedTestCnClient.ParseNodes("{\"data\":\"invalid\"}"));
    return Task.CompletedTask;
});
await Run("Speedtest.net catalogs select HTTPS aliases, filter invalid nodes and support XML", () =>
{
    var parsed = SpeedTestNetClient.ParseNodes("""
    [
      {"id":"1","url":"http://old.example:8080/speedtest/upload.php","host":"tls.example:8443","sponsor":"Example ISP","country":"China","name":"Shanghai","distance":20,"https_functional":1},
      {"id":"1","url":"https://duplicate.example/speedtest/upload.php"},
      {"id":"2","url":"https://example.com/speedtest/upload.php","https_functional":0},
      {"id":"3","url":"file:///C:/upload.php"},
      {"id":"4","url":"https://user:pass@example.com/speedtest/upload.php"},
      {"id":"5","url":"https://example.com/speedtest/upload.php","host":"example.com/other-path"},
      {"id":"6","url":"https://example.com/login"},
      {"id":"9","url":"https://example.com/speedtest/upload.php","https_functional":false},
      {"id":"7","url":"http://fallback.example/speedtest/upload.php","distance":1}
    ]
    """);
    Check(parsed.Count == 2 && parsed[0].Id == "7", "Invalid or duplicate server accepted, or distance sort failed");
    var node = parsed.Single(n => n.Id == "1");
    Check(node.Protocol == SpeedProtocol.SpeedTestNet && node.UploadUri.AbsoluteUri == "https://tls.example:8443/speedtest/upload.php" &&
        node.DownloadUri.AbsoluteUri == "https://tls.example:8443/speedtest/random2000x2000.jpg" && node.PingUri.AbsolutePath.EndsWith("latency.txt"), "HTTPS alias/protocol mapping incorrect");
    Check(parsed[0].UploadUri.Port == 443, "Default HTTP port was not upgraded");
    var xml = SpeedTestNetClient.ParseNodes("<settings><servers><server id='8' url='http://example.com:8080/speedtest/upload.php' host='example.com:8080' sponsor='XML ISP' /></servers></settings>");
    Check(xml.Single().Id == "8" && xml[0].UploadUri.Scheme == "https", "XML fallback mapping failed");
    Throws<HttpRequestException>(() => SpeedTestNetClient.ParseNodes("<!DOCTYPE settings [<!ENTITY x SYSTEM 'file:///C:/example'>]><settings>&x;</settings>"));
    Throws<HttpRequestException>(() => SpeedTestNetClient.ParseNodes("<html>not a catalog"));
    Throws<HttpRequestException>(() => SpeedTestNetClient.ParseNodes("{\"error\":true}"));
    return Task.CompletedTask;
});
await Run("Speedtest.net download reads JPEG bytes without a bytes/size parameter and respects a tiny final remainder", async () =>
{
    const long cap = 32768 + 2;
    var result = await new SpeedTestService().RunPhaseAsync(new(server.Uri("net-down"), null, 3, 4, cap, Protocol: SpeedProtocol.SpeedTestNet), TrafficPhase.Download, null, default);
    Check(result.Bytes == cap && result.ReachedLimit && result.Errors.Count == 0, "JPEG byte accounting or query mapping failed");
    await ThrowsAsync<HttpRequestException>(() => new SpeedTestService().RunPhaseAsync(new(server.Uri("down"), null, 2, 1, cap, Protocol: SpeedProtocol.SpeedTestNet), TrafficPhase.Download, null, default));
});
await Run("Speedtest.net uploads require a matching server acknowledgement", async () =>
{
    const long cap = 1024 * 1024 + 19;
    var result = await new SpeedTestService().RunPhaseAsync(new(server.Uri("net-down"), server.Uri("net-up"), 3, 4, cap, Protocol: SpeedProtocol.SpeedTestNet), TrafficPhase.Upload, null, default);
    Check(result.Bytes == cap && result.Errors.Count == 0 && result.ReachedLimit, "Acknowledged upload accounting failed");
    foreach (var path in new[] { "net-bad-ack", "net-html-ack" })
        await ThrowsAsync<HttpRequestException>(() => new SpeedTestService().RunPhaseAsync(new(server.Uri("net-down"), server.Uri(path), 2, 1, 1024, Protocol: SpeedProtocol.SpeedTestNet), TrafficPhase.Upload, null, default));
});
await Run("Speedtest.net ranking rejects successful HTTP pages without the latency signature", async () =>
{
    var healthy = new SpeedTestNode("1", "local", "", "", "", server.Uri("net-down"), server.Uri("net-up"), server.Uri("net-ping"), 1, SpeedProtocol.SpeedTestNet);
    var captive = healthy with { Id = "2", PingUri = server.Uri("net-html-ack") };
    var ranked = await new SpeedTestCnClient().RankNodesAsync([captive, healthy], false, default);
    Check(ranked[0].Node.Id == "1" && ranked[0].LatencyMs.HasValue && !ranked[1].LatencyMs.HasValue, "Captive page incorrectly marked reachable");
});
await Run("SpeedTest.cn node ranking keeps reachable nodes ahead of failed nodes", async () =>
{
    var healthy = new SpeedTestNode("1", "local", "", "", "", server.Uri("down"), server.Uri("up"), server.Uri("hello"), 1);
    var failedNode = healthy with { Id = "2", PingUri = server.Uri("fail") };
    var result = await new SpeedTestCnClient().RankNodesAsync([failedNode, healthy], false, default);
    Check(result[0].Node.Id == "1" && result[0].LatencyMs.HasValue && !result[1].LatencyMs.HasValue, "Node ranking incorrect");
});
await Run("Periodic sync: exact due time, busy deferral, and immediate run after deferral", () =>
{
    var at = DateTimeOffset.Parse("2026-10-18T00:00:00+08:00");
    var schedule = new NodeSyncSchedule(new(true, 15), at);
    Check(!schedule.IsDue(at.AddMinutes(14)), "Sync ran too early");
    Check(schedule.IsDue(at.AddMinutes(15)) && !schedule.CanRun(at.AddMinutes(15), busy: true), "Busy sync was not deferred");
    Check(schedule.CanRun(at.AddMinutes(17), busy: false), "Deferred sync should remain due");
    schedule.Succeeded(at.AddMinutes(17));
    Check(schedule.NextAttempt == at.AddMinutes(32) && !schedule.IsDue(at.AddMinutes(18)), "Successful sync cadence incorrect");
    return Task.CompletedTask;
});
await Run("Periodic sync: failure preserves success time and waits one minute before retry", () =>
{
    var at = DateTimeOffset.Parse("2026-10-18T00:00:00+08:00");
    var schedule = new NodeSyncSchedule(new(), at);
    schedule.Failed(at.AddMinutes(15));
    Check(schedule.LastSuccess == at && !schedule.IsDue(at.AddMinutes(15).AddSeconds(59)) && schedule.IsDue(at.AddMinutes(16)), "Failure backoff incorrect");
    schedule.Configure(new(false, 30));
    Check(!schedule.CanRun(at.AddDays(2), false) && schedule.NextAttempt == null, "Disabled sync should not run");
    schedule.Succeeded(at.AddMinutes(20));
    schedule.Configure(new(true, 60));
    Check(schedule.NextAttempt == at.AddMinutes(80), "Changed interval did not use last successful sync");
    schedule.RequestNow();
    Check(schedule.IsDue(at.AddMinutes(21)), "Explicit refresh did not invalidate the deadline");
    return Task.CompletedTask;
});
await Run("Node settings and IP cache survive reopening; invalid saves preserve the last good file", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "NetPulse-test-" + Guid.NewGuid().ToString("N"));
    var storage = new NodeCatalogStorage(directory);
    try
    {
        Check(storage.LoadSettings() == new NodeSyncSettings() && storage.LoadCatalog() == null, "Missing-file defaults incorrect");
        storage.SaveSettings(new(false, 30, true));
        var node = new SpeedTestNode("saved", "本地测试", "", "", "", server.Uri("down"), server.Uri("up"), server.Uri("hello"), 1);
        var snapshot = new NodeCatalogSnapshot(DateTimeOffset.UtcNow, true, [new(node, 2.5, ["127.0.0.1", "::1"])], "saved");
        storage.SaveCatalog(snapshot);
        var reopened = new NodeCatalogStorage(directory);
        var loaded = reopened.LoadCatalog();
        Check(reopened.LoadSettings() == new NodeSyncSettings(false, 30, true), "Settings did not round-trip");
        Check(loaded?.SelectedNodeId == "saved" && loaded.Nodes[0].IpAddresses!.SequenceEqual(new[] { "127.0.0.1", "::1" }) && loaded.SyncedAt == snapshot.SyncedAt, "IP cache did not round-trip");
        Throws<ArgumentException>(() => storage.SaveCatalog(snapshot with { Nodes = [] }));
        Throws<ArgumentException>(() => storage.SaveSettings(new(true, 0)));
        Check(reopened.LoadCatalog()?.Nodes.Count == 1 && reopened.LoadSettings().IntervalMinutes == 30, "Rejected save damaged previous cache");
        Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "Atomic-save temporary file leaked");
        File.WriteAllText(Path.Combine(directory, "nodes-v1.json"), "{bad json");
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"IntervalMinutes\":999}");
        Check(reopened.LoadCatalog() == null && reopened.LoadSettings() == new NodeSyncSettings(), "Corrupt local files should be ignored");
    }
    finally
    {
        foreach (var name in new[] { "nodes-v1.json", "settings.json" }) File.Delete(Path.Combine(directory, name));
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }
    return Task.CompletedTask;
});
await Run("Node IP sync resolves real loopback DNS and supports cancellation", async () =>
{
    var node = new SpeedTestNode("dns", "local", "", "", "", server.Uri("down"), server.Uri("up"), server.Uri("hello"), 1);
    var resolved = await NodeIpResolver.ResolveAsync([new(node, 1)], default);
    Check(resolved.Single().IpAddresses?.Contains("127.0.0.1") == true, "IP address was not resolved");
    var localhost = node with { DownloadUri = new($"http://localhost:{server.Port}/down") };
    var named = await NodeIpResolver.ResolveAsync([new(localhost, 1)], default);
    Check(named.Single().IpAddresses is { Count: > 0 }, "Hostname was not resolved");
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => NodeIpResolver.ResolveAsync([new(node, 1)], canceled.Token));
});
await Run("Provider caches are isolated and legacy settings/cache remain readable", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "NetPulse-provider-test-" + Guid.NewGuid().ToString("N"));
    var cn = new NodeCatalogStorage(directory);
    var net = new NodeCatalogStorage(directory, NodeCatalogProvider.SpeedTestNet);
    try
    {
        var node = new SpeedTestNode("same-id", "CN", "", "", "", server.Uri("down"), server.Uri("up"), server.Uri("hello"), 1);
        cn.SaveCatalog(new(DateTimeOffset.UtcNow, false, [new(node, 1)]));
        var netNode = node with { Name = "NET", Protocol = SpeedProtocol.SpeedTestNet };
        net.SaveCatalog(new(DateTimeOffset.UtcNow, false, [new(netNode, 2)], Provider: NodeCatalogProvider.SpeedTestNet));
        Check(cn.LoadCatalog()?.Nodes.Single().Node.Name == "CN" && net.LoadCatalog()?.Nodes.Single().Node.Name == "NET", "Provider cache collision");
        Throws<ArgumentException>(() => net.SaveCatalog(cn.LoadCatalog()!));
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"Enabled\":false,\"IntervalMinutes\":30,\"UseProxy\":true}");
        Check(net.LoadSettings() == new NodeSyncSettings(false, 30, true, NodeCatalogProvider.SpeedTestNet), "Legacy settings did not preserve options with new default provider");
        var legacy = File.ReadAllText(Path.Combine(directory, "nodes-v1.json"));
        var legacyObject = System.Text.Json.Nodes.JsonNode.Parse(legacy)!.AsObject();
        legacyObject.Remove("Provider");
        legacyObject["Nodes"]![0]!["Node"]!.AsObject().Remove("Protocol");
        File.WriteAllText(Path.Combine(directory, "nodes-v1.json"), legacyObject.ToJsonString());
        Check(cn.LoadCatalog()?.Nodes[0].Node.Protocol == SpeedProtocol.SpeedTestCn, "Legacy node cache not restored");
    }
    finally
    {
        foreach (var name in new[] { "nodes-v1.json", "nodes-speedtest-net-v1.json", "settings.json" }) File.Delete(Path.Combine(directory, name));
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }
    return Task.CompletedTask;
});
await Run("Small download responses continue until actual traffic cap", async () =>
{
    const long cap = 32 * 1024;
    var result = await new SpeedTestService().RunPhaseAsync(new(server.Uri("small"), null, 4, 2, cap), TrafficPhase.Download, null, default);
    Check(result.Bytes == cap && result.ReachedLimit, $"Small response stopped early: {result.Bytes}");
});
await Run("Upload counts only server-acknowledged bodies", async () =>
{
    const long cap = 3 * 1024 * 1024 + 113;
    var before = server.UploadBytes;
    var result = await new SpeedTestService().RunPhaseAsync(new(server.Uri("down"), server.Uri("up"), 3, 4, cap), TrafficPhase.Upload, null, default);
    Check(result.Bytes == cap && server.UploadBytes - before == cap && result.ReachedLimit && result.Errors.Count == 0, "Acknowledged upload count incorrect");
});
await Run("HTTP error, HTML, empty body, and rejected upload produce errors", async () =>
{
    foreach (var path in new[] { "fail", "html", "empty" })
        await ThrowsAsync<HttpRequestException>(() => new SpeedTestService().RunPhaseAsync(new(server.Uri(path), null, 1, 2, 4096), TrafficPhase.Download, null, default));
    await ThrowsAsync<HttpRequestException>(() => new SpeedTestService().RunPhaseAsync(new(server.Uri("down"), server.Uri("reject"), 1, 2, 4096), TrafficPhase.Upload, null, default));
});
await Run("Total duration cancels a stalled HTTP response", async () =>
{
    var watch = Stopwatch.StartNew();
    await ThrowsAsync<HttpRequestException>(() => new SpeedTestService().RunPhaseAsync(new(server.Uri("stall"), null, 1, 2, 4096), TrafficPhase.Download, null, default));
    Check(watch.Elapsed.TotalSeconds < 3, "Deadline not respected");
});
await Run("User cancellation interrupts live transfer and does not return a result", async () =>
{
    using var cts = new CancellationTokenSource(250);
    var watch = Stopwatch.StartNew();
    await ThrowsAsync<OperationCanceledException>(() => new SpeedTestService().RunPhaseAsync(new(server.Uri("slow"), null, 10, 2, 1024 * 1024), TrafficPhase.Download, null, cts.Token));
    Check(watch.Elapsed.TotalSeconds < 2, "Cancellation too slow");
});
await Run("Live progress reports real transfer and bounded fractions", async () =>
{
    var updates = new ConcurrentQueue<SpeedProgress>();
    var result = await new SpeedTestService().RunPhaseAsync(new(server.Uri("slow"), null, 1, 1, 1024 * 1024), TrafficPhase.Download, new InlineProgress<SpeedProgress>(updates.Enqueue), default);
    Check(result.Bytes > 0 && !updates.IsEmpty && updates.All(p => p.Fraction is >= 0 and <= 1 && p.MegabitsPerSecond >= 0), "Progress invalid");
});
await Run("GitHub stable releases: numeric versions and official repository validation", () =>
{
    var fixture = ReleaseFixture("2.10.0");
    var parsed = GitHubUpdateClient.ParseGitHubRelease(GitHubFixture(fixture));
    Check(parsed.IsNewerThan(new Version(2, 9, 0)) && !parsed.IsNewerThan(new Version(2, 10, 0, 0)), "Version comparison incorrect");
    Throws<ArgumentException>(() => GitHubUpdateClient.Validate(fixture with { DownloadUrl = "https://evil.example/NetPulse.exe" }));
    Throws<ArgumentException>(() => GitHubUpdateClient.Validate(fixture with { Sha256 = "bad" }));
    Throws<ArgumentException>(() => GitHubUpdateClient.Validate(fixture with { Version = "3.0.0-beta" }));
    Throws<InvalidDataException>(() => GitHubUpdateClient.ParseGitHubRelease(GitHubFixture(fixture, prerelease: true)));
    return Task.CompletedTask;
});
await Run("GitHub update checks are anonymous and accept a complete published EXE asset", async () =>
{
    var fixture = ReleaseFixture();
    var client = new GitHubUpdateClient(() => new HttpClient(new StubHandler((request, token) =>
    {
        Check(request.RequestUri!.AbsoluteUri == GitHubUpdateClient.LatestApiUrl && !request.Headers.Contains("Authorization"), "Unexpected authenticated or non-official request");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(GitHubFixture(fixture)) });
    })));
    Check((await client.CheckAsync(default))?.Version == fixture.Version, "Published release was not found");
});
await Run("GitHub connection failures fall back to the official raw version manifest", async () =>
{
    var fixture = ReleaseFixture(); var calls = 0;
    var client = new GitHubUpdateClient(() => new HttpClient(new StubHandler((request, token) =>
    {
        calls++;
        if (request.RequestUri!.AbsoluteUri == GitHubUpdateClient.LatestApiUrl) throw new HttpRequestException("Simulated API outage");
        Check(request.RequestUri.AbsoluteUri == GitHubUpdateClient.ManifestUrl, "Fallback left official GitHub hosts");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(fixture)) });
    })));
    Check((await client.CheckAsync(default))?.Version == fixture.Version && calls == 2, "Fallback failed");
});
await Run("GitHub incomplete or prerelease metadata fails instead of pretending to be current", async () =>
{
    var calls = 0;
    var client = new GitHubUpdateClient(() => new HttpClient(new StubHandler((request, token) =>
    {
        calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(GitHubFixture(ReleaseFixture(), prerelease: true)) });
    })));
    await ThrowsAsync<InvalidDataException>(() => client.CheckAsync(default));
    Check(calls == 1, "Invalid metadata should not fall back to older information");
    var empty = new GitHubUpdateClient(() => new HttpClient(new StubHandler((request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))));
    Check(await empty.CheckAsync(default) == null, "Empty repository should report no release");
});
await Run("Update downloads verify SHA-256 and preserve the destination on tampering or wrong size", async () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "NetPulse-download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, "NetPulse.exe");
    var bytes = Encoding.UTF8.GetBytes("test-executable-payload");
    var release = ReleaseFixture() with { Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
    var client = new GitHubUpdateClient(() => new HttpClient(new StubHandler((request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }))));
    try
    {
        await client.DownloadAsync(release, path, null, default);
        Check(File.ReadAllBytes(path).SequenceEqual(bytes), "Verified download was not saved");
        File.WriteAllText(path, "previous-good-file");
        await ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(release with { Sha256 = new string('0', 64) }, path, null, default));
        await ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(release with { Size = bytes.Length + 1 }, path, null, default));
        Check(File.ReadAllText(path) == "previous-good-file" && Directory.GetFiles(folder).Length == 1, "Failed download modified the destination or leaked partial files");
    }
    finally { File.Delete(path); Directory.Delete(folder); }
});
await Run("Canceling an update download removes the partial file and keeps the existing file", async () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "NetPulse-cancel-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, "NetPulse.exe"); File.WriteAllText(path, "old");
    var client = new GitHubUpdateClient(() => new HttpClient(new StubHandler((request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DelayedReadStream()) }))));
    try
    {
        using var canceled = new CancellationTokenSource(100);
        await ThrowsAsync<OperationCanceledException>(() => client.DownloadAsync(ReleaseFixture(), path, null, canceled.Token));
        Check(File.ReadAllText(path) == "old" && Directory.GetFiles(folder).Length == 1, "Canceled update damaged a file");
    }
    finally { File.Delete(path); Directory.Delete(folder); }
});
await Run("Update check schedule and cached newer release survive reopening", () =>
{
    var at = DateTimeOffset.UtcNow; var state = new UpdateCheckState(true, at, null, ReleaseFixture());
    Check(!state.IsDue(at.AddHours(5)) && state.IsDue(at.AddHours(6)) && !state.IsDue(at.AddHours(7), busy: true), "Six-hour schedule incorrect");
    Check(!(state with { Enabled = false }).IsDue(at.AddDays(1)) && !(state with { RetryAfter = at.AddMinutes(15) }).IsDue(at.AddMinutes(14)), "Disabled state or retry backoff incorrect");
    var folder = Path.Combine(Path.GetTempPath(), "NetPulse-updates-" + Guid.NewGuid().ToString("N"));
    try
    {
        new UpdateStateStorage(folder).Save(state);
        var loaded = new UpdateStateStorage(folder).Load();
        Check(loaded.Release?.Version == state.Release!.Version && loaded.LastCheckedAt == at, "Update state did not persist");
        File.WriteAllText(Path.Combine(folder, "updates.json"), "bad-json");
        Check(new UpdateStateStorage(folder).Load() == new UpdateCheckState(), "Corrupt update state should use defaults");
    }
    finally { File.Delete(Path.Combine(folder, "updates.json")); if (Directory.Exists(folder)) Directory.Delete(folder); }
    return Task.CompletedTask;
});
await Run("Automatic installation waits for idle, respects preferences, records and retry cooldown", () =>
{
    var now = DateTimeOffset.UtcNow;
    var state = new UpdateCheckState();
    Check(SelfUpdate.CanAutoInstall(state, false, false, now), "Default automatic install is disabled");
    Check(!SelfUpdate.CanAutoInstall(state, true, false, now) && !SelfUpdate.CanAutoInstall(state, false, true, now), "Active work or records were interrupted");
    Check(!SelfUpdate.CanAutoInstall(state with { AutoInstall = false }, false, false, now) &&
        !SelfUpdate.CanAutoInstall(state with { Enabled = false }, false, false, now), "Disabled preference ignored");
    Check(!SelfUpdate.CanAutoInstall(state with { RetryAfter = now.AddMinutes(15) }, false, false, now), "Failure cooldown ignored");
    Check(JsonSerializer.Deserialize<UpdateCheckState>("{\"Enabled\":false}") is { Enabled: false, AutoInstall: true }, "Legacy update preferences did not migrate");
    return Task.CompletedTask;
});
await Run("Self-update request rejects wrong handoff tokens, stale requests and noncanonical targets", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "NetPulse-handoff-" + Guid.NewGuid().ToString("N"));
    var id = Guid.NewGuid().ToString("N");
    var transactionDirectory = Path.Combine(directory, "updates", id);
    Directory.CreateDirectory(transactionDirectory);
    var path = Path.Combine(transactionDirectory, "request.json");
    var request = new SelfUpdateRequest(id, new string('A', 64), Path.Combine(directory, "NetPulse.exe"), new string('B', 64), 100,
        Environment.ProcessId, DateTime.UtcNow.Ticks, ReleaseFixture(), directory, [], DateTimeOffset.UtcNow);
    try
    {
        File.WriteAllText(path, JsonSerializer.Serialize(request));
        Check(SelfUpdate.ReadTransaction(path, request.Nonce).Request.Id == id, "Valid request rejected");
        Throws<InvalidDataException>(() => SelfUpdate.ReadTransaction(path, new string('C', 64)));
        File.WriteAllText(path, JsonSerializer.Serialize(request with { CreatedAt = DateTimeOffset.UtcNow.AddHours(-1) }));
        Throws<InvalidDataException>(() => SelfUpdate.ReadTransaction(path, request.Nonce));
        File.WriteAllText(path, JsonSerializer.Serialize(request with { TargetPath = Path.Combine(directory, "sub", "..", "NetPulse.exe") }));
        Throws<InvalidDataException>(() => SelfUpdate.ReadTransaction(path, request.Nonce));
    }
    finally { File.Delete(path); Directory.Delete(transactionDirectory); Directory.Delete(Path.GetDirectoryName(transactionDirectory)!); Directory.Delete(directory); }
    return Task.CompletedTask;
});
await Run("Self-update rejects a hash-valid payload that is not a Windows executable", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), "NetPulse-invalid-" + Guid.NewGuid().ToString("N") + ".exe");
    var bytes = Encoding.UTF8.GetBytes("not an executable");
    File.WriteAllBytes(path, bytes);
    try
    {
        var release = ReleaseFixture() with { Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        await ThrowsAsync<BadImageFormatException>(() => SelfUpdate.ValidatePackageAsync(path, release, default));
        await ThrowsAsync<InvalidDataException>(() => SelfUpdate.ValidatePackageAsync(path, release with { Sha256 = new string('0', 64) }, default));
    }
    finally { File.Delete(path); }
});
Console.WriteLine($"\n{passed} passed, {failed} failed.");
if (failed == 0 && args.Contains("--compare-online"))
{
    var comparisons = new List<object>();
    try
    {
        var provider = new SpeedTestCnClient();
        var catalog = await provider.GetNodesAsync(false, default);
        var ranked = await provider.RankNodesAsync(catalog, false, default);
        Console.WriteLine($"SpeedTest.cn: {catalog.Count} public nodes, {ranked.Count(n => n.LatencyMs.HasValue)} reachable.");
        foreach (var candidate in ranked) Console.WriteLine("  " + candidate.DisplayName);
        var selected = ranked.FirstOrDefault(n => n.LatencyMs.HasValue) ?? throw new Exception("No reachable SpeedTest.cn node");
        var cn = new SpeedOptions(selected.Node.DownloadUri, selected.Node.UploadUri, 5, 4, 64L * 1024 * 1024, Protocol: SpeedProtocol.SpeedTestCn);
        var cloudflare = new SpeedOptions(new("https://speed.cloudflare.com/__down"), new("https://speed.cloudflare.com/__up"), 5, 4, 64L * 1024 * 1024);
        foreach (var entry in new[] { (Name: "SpeedTest.cn / " + selected.Node.DisplayName, Options: cn), (Name: "Cloudflare", Options: cloudflare) })
        {
            foreach (var phase in new[] { TrafficPhase.Download, TrafficPhase.Upload })
            {
                try
                {
                    var result = await new SpeedTestService().RunPhaseAsync(entry.Options, phase, null, default);
                    comparisons.Add(new { Source = entry.Name, Phase = phase.ToString(), result.MegabitsPerSecond, result.Bytes, result.ElapsedSeconds, result.ReachedLimit, result.Errors });
                    Console.WriteLine($"{entry.Name} / {phase}: {result.MegabitsPerSecond:F1} Mbps, {result.Bytes} bytes, {result.ElapsedSeconds:F2}s");
                }
                catch (Exception ex) { comparisons.Add(new { Source = entry.Name, Phase = phase.ToString(), Error = ex.Message }); failed++; Console.WriteLine(ex.Message); }
            }
        }
    }
    catch (Exception ex) { comparisons.Add(new { Error = ex.ToString() }); failed++; Console.WriteLine(ex); }
    Directory.CreateDirectory("artifacts/speedtest-cn");
    File.WriteAllText("artifacts/speedtest-cn/comparison.json", JsonSerializer.Serialize(comparisons, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
}
if (failed == 0 && args.Contains("--verify-github"))
{
    try
    {
        var client = new GitHubUpdateClient();
        var release = await client.CheckAsync(default) ?? throw new Exception("No public GitHub release");
        var project = System.Xml.Linq.XDocument.Load("src/NetPulse/NetPulse.csproj");
        var current = Version.Parse(project.Descendants("Version").Single().Value);
        Check(release.Version == current.ToString(3) && release.IsNewerThan(new Version(1, 2, 0)) && release.IsNewerThan(new Version(1, 3, 0)) && !release.IsNewerThan(current), "Published version or old-client upgrade detection mismatch");
        Directory.CreateDirectory("artifacts/github-client");
        await client.DownloadAsync(release, "artifacts/github-client/NetPulse.exe", null, default);
        File.WriteAllText("artifacts/github-client/verification.json", JsonSerializer.Serialize(new { release.Version, release.Size, release.Sha256, OldClientDetectsUpdate = true, CurrentClientIsLatest = true, DownloadVerified = true }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"GitHub release v{release.Version}: old-client detection and real EXE download/hash verification passed.");
    }
    catch (Exception ex) { failed++; Console.WriteLine(ex); }
}
return failed == 0 ? 0 : 1;

static GitHubReleaseInfo ReleaseFixture(string version = "2.0.0") => new(version,
    $"https://github.com/{GitHubUpdateClient.Repository}/releases/download/v{version}/NetPulse.exe",
    $"https://github.com/{GitHubUpdateClient.Repository}/releases/tag/v{version}", 100, new string('A', 64), "Test release notes", DateTimeOffset.UtcNow);
static string GitHubFixture(GitHubReleaseInfo release, bool prerelease = false) => JsonSerializer.Serialize(new
{
    tag_name = "v" + release.Version, draft = false, prerelease, html_url = release.ReleaseUrl, published_at = release.PublishedAt, body = release.Notes,
    assets = new[] { new { name = "NetPulse.exe", browser_download_url = release.DownloadUrl, size = release.Size, digest = "sha256:" + release.Sha256 } }
});

async Task Run(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + "\n" + ex); }
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Throws<T>(Action action) where T : Exception
{ try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{ try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{ public void Report(T value) => action(value); }

sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken); }
sealed class DelayedReadStream : Stream
{
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { await Task.Delay(10000, cancellationToken); return 0; }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class LocalServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly ConcurrentBag<Task> _clients = [];
    private long _uploadBytes;
    public long UploadBytes => Interlocked.Read(ref _uploadBytes);
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public Uri Uri(string path) => new($"http://127.0.0.1:{Port}/{path}");
    public LocalServer() { _listener.Start(); _loop = AcceptAsync(); }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _clients.Add(HandleAsync(client));
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var token = _cts.Token;
                var stream = client.GetStream();
                var header = new List<byte>(); var single = new byte[1];
                while (header.Count < 16384 && await stream.ReadAsync(single, token) != 0)
                {
                    header.Add(single[0]);
                    if (header.Count >= 4 && Encoding.ASCII.GetString(header.TakeLast(4).ToArray()) == "\r\n\r\n") break;
                }
                if (header.Count == 0) return; // TCP latency probe without an HTTP request.
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var parts = lines[0].Split(' ');
                if (parts.Length < 2) return;
                var uri = new Uri("http://localhost" + parts[1]);
                var path = uri.AbsolutePath;
                if (path.StartsWith("/net-") && (uri.Query.Contains("bytes=") || uri.Query.Contains("size=")))
                { await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token); return; }
                if (path == "/stall") { await Task.Delay(Timeout.Infinite, token); return; }
                var lengthLine = lines.FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var length = lengthLine == null ? 0 : int.Parse(lengthLine.Split(':')[1].Trim());
                var buffer = new byte[64 * 1024];
                var remaining = length;
                while (remaining > 0)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), token);
                    if (n == 0) return;
                    remaining -= n;
                }
                if (path == "/up") Interlocked.Add(ref _uploadBytes, length);
                if (path is "/net-up" or "/net-bad-ack" or "/net-html-ack" or "/net-ping")
                {
                    var answer = path switch { "/net-ping" => "test=test\n", "/net-bad-ack" => "size=0\n", "/net-html-ack" => "<html>login</html>", _ => $"size={length}\n" };
                    if (path == "/net-up" && !lines.Any(l => l.Equals("Content-Type: application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))) answer = "wrong upload type";
                    var body = Encoding.ASCII.GetBytes(answer);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), token);
                    await stream.WriteAsync(body, token);
                    return;
                }
                if (path is "/fail" or "/reject")
                { await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 503 Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token); return; }
                var parameter = path == "/cn-down" ? "size=" : "bytes=";
                var bytesField = uri.Query.TrimStart('?').Split('&').SingleOrDefault(f => f.StartsWith(parameter));
                var amount = bytesField == null ? 0 : int.Parse(bytesField.Split('=')[1]);
                if (path == "/small") amount = Math.Min(1024, amount);
                if (path == "/net-down") { amount = 8192; buffer[0] = 0xff; buffer[1] = 0xd8; buffer[2] = 0xff; }
                if (path == "/empty" || path == "/up") amount = 0;
                var contentType = path == "/html" ? "text/html" : "application/octet-stream";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {amount}\r\nConnection: close\r\n\r\n"), token);
                while (amount > 0)
                {
                    var n = Math.Min(path == "/slow" ? 1024 : buffer.Length, amount);
                    await stream.WriteAsync(buffer.AsMemory(0, n), token);
                    amount -= n;
                    if (path == "/slow") await Task.Delay(100, token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
        }
    }
    public async ValueTask DisposeAsync()
    { _cts.Cancel(); _listener.Stop(); await _loop; await Task.WhenAll(_clients); _cts.Dispose(); }
}
