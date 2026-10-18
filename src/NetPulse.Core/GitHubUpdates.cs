using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetPulse.Core;

public sealed record GitHubReleaseInfo(string Version, string DownloadUrl, string ReleaseUrl, long Size,
    string Sha256, string Notes, DateTimeOffset PublishedAt)
{
    public bool IsNewerThan(Version current) => System.Version.Parse(Version) > Normalize(current);
    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));
}

public sealed record UpdateCheckState(bool Enabled = true, DateTimeOffset? LastCheckedAt = null,
    DateTimeOffset? RetryAfter = null, GitHubReleaseInfo? Release = null)
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    public bool IsDue(DateTimeOffset now, bool busy = false) => Enabled && !busy &&
        (RetryAfter is { } retry ? now >= retry : LastCheckedAt == null || now >= LastCheckedAt.Value + Interval);
}

public sealed class UpdateStateStorage(string directory)
{
    private readonly string _path = Path.Combine(Path.GetFullPath(directory), "updates.json");
    public UpdateCheckState Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 1024 * 1024) return new();
            var state = JsonSerializer.Deserialize<UpdateCheckState>(File.ReadAllText(_path));
            if (state?.Release != null) GitHubUpdateClient.Validate(state.Release);
            return state ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return new(); }
    }
    public void Save(UpdateCheckState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(state)); File.Move(temporary, _path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class GitHubUpdateClient
{
    public const string Repository = "boooooommmmmm/NetSpeedTest";
    public const string ReleasesUrl = "https://github.com/" + Repository + "/releases";
    public const string LatestApiUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const string ManifestUrl = "https://raw.githubusercontent.com/" + Repository + "/main/release/latest.json";
    public const string AssetName = "NetPulse.exe";
    public const long MaxExeBytes = 256L * 1024 * 1024;
    private readonly Func<HttpClient> _createClient;

    public GitHubUpdateClient(Func<HttpClient>? createClient = null) => _createClient = createClient ?? (() => new HttpClient(new SocketsHttpHandler
    { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli, ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = Timeout.InfiniteTimeSpan });

    public async Task<GitHubReleaseInfo?> CheckAsync(CancellationToken token)
    {
        using var client = CreateClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await client.GetAsync(LatestApiUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return ParseGitHubRelease(await ReadJsonAsync(response, timeout.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is not InvalidDataException && e is HttpRequestException or IOException or OperationCanceledException)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await client.GetAsync(ManifestUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var release = JsonSerializer.Deserialize<GitHubReleaseInfo>(await ReadJsonAsync(response, timeout.Token).ConfigureAwait(false))
                ?? throw new InvalidDataException("GitHub 更新清单为空。");
            Validate(release);
            return release;
        }
    }

    public static GitHubReleaseInfo ParseGitHubRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
            root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
            throw new InvalidDataException("更新来源返回了未正式发布的版本。");
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = tag.StartsWith('v') ? tag[1..] : tag;
        var asset = root.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == AssetName);
        if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("GitHub 最新版本尚无完整的单文件 EXE 附件。");
        var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() ?? "" : "";
        var release = new GitHubReleaseInfo(version, asset.GetProperty("browser_download_url").GetString() ?? "",
            root.GetProperty("html_url").GetString() ?? "", asset.GetProperty("size").GetInt64(),
            digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..] : "",
            root.TryGetProperty("body", out var notes) ? notes.GetString() ?? "" : "",
            root.GetProperty("published_at").GetDateTimeOffset());
        Validate(release);
        return release;
    }

    public static void Validate(GitHubReleaseInfo release)
    {
        if (!Regex.IsMatch(release.Version ?? "", @"^\d+\.\d+\.\d+$") || !Version.TryParse(release.Version, out _))
            throw new ArgumentException("GitHub 发布版本号无效。");
        if (release.Size is <= 0 or > MaxExeBytes || !Regex.IsMatch(release.Sha256 ?? "", "^[A-Fa-f0-9]{64}$"))
            throw new ArgumentException("GitHub 附件缺少有效的大小或 SHA-256 校验信息。");
        if (!IsOfficialUrl(release.DownloadUrl, $"/{Repository}/releases/download/v{release.Version}/{AssetName}") ||
            !IsOfficialUrl(release.ReleaseUrl, $"/{Repository}/releases/tag/v{release.Version}"))
            throw new ArgumentException("更新链接必须指向官方 NetSpeedTest 仓库的对应版本。");
    }

    private static bool IsOfficialUrl(string value, string path) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == "github.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath.Equals(path, StringComparison.Ordinal);

    public async Task DownloadAsync(GitHubReleaseInfo release, string destination, IProgress<double>? progress, CancellationToken token)
    {
        Validate(release);
        var path = Path.GetFullPath(destination);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".download";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != release.Size)
                throw new InvalidDataException("下载文件大小与 GitHub 发布信息不一致。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            var buffer = new byte[64 * 1024];
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
            {
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    total += count;
                    if (total > release.Size) throw new InvalidDataException("下载文件超过发布声明的大小。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    progress?.Report(total * 100d / release.Size);
                }
            }
            if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("下载文件 SHA-256 或大小校验失败，请重试。");
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private HttpClient CreateClient()
    {
        var client = _createClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NetPulse-Update/1.3");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.CacheControl = new() { NoCache = true };
        return client;
    }
    private static async Task<string> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > 1024 * 1024) throw new InvalidDataException("GitHub 更新信息超过允许大小。");
            output.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }
}
