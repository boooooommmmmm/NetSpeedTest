using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace NetPulse.Core;

public sealed record SelfUpdateRequest(string Id, string Nonce, string TargetPath, string OldSha256,
    long OldSize, int ParentId, long ParentStartTicks, GitHubReleaseInfo Release, string WorkingDirectory,
    string[] RestartArguments, DateTimeOffset CreatedAt);
public sealed record SelfUpdateResult(string Version, string TargetPath, string Status, string Message, DateTimeOffset At);
public sealed record UpdateStartupAck(string Nonce, int ProcessId, string Version);

public sealed class SelfUpdateTransaction
{
    public SelfUpdateRequest Request { get; }
    public string DirectoryPath { get; }
    public string RequestPath => Path.Combine(DirectoryPath, "request.json");
    public string HelperPath => Path.Combine(DirectoryPath, "NetPulse.Updater.exe");
    public string ReadyPath => Path.Combine(DirectoryPath, "ready.json");
    public string AckPath => Path.Combine(DirectoryPath, "started.json");
    public string AbortPath => Path.Combine(DirectoryPath, "abort");
    public string ResultPath => Path.Combine(DirectoryPath, "result.json");
    public string LastResultPath => Path.Combine(Path.GetDirectoryName(DirectoryPath)!, "last-result.json");
    public string CandidatePath => Path.Combine(Path.GetDirectoryName(Request.TargetPath)!, $".NetPulse-update-{Request.Id}.new");
    public string BackupPath => Path.Combine(Path.GetDirectoryName(Request.TargetPath)!, $".NetPulse-update-{Request.Id}.bak");
    public string SessionBackupPath => Path.Combine(Path.GetDirectoryName(DirectoryPath)!, $"session-{Request.Id}.csv");

    internal SelfUpdateTransaction(SelfUpdateRequest request, string directory)
    { Request = request; DirectoryPath = Path.GetFullPath(directory); }
}

/// <summary>The distributed EXE runs a temporary copy of itself to replace its locked image.</summary>
public static class SelfUpdate
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool CanAutoInstall(UpdateCheckState state, bool busy, bool hasRecords, DateTimeOffset now) =>
        state.Enabled && state.AutoInstall && !busy && !hasRecords && (state.RetryAfter == null || now >= state.RetryAfter);

    public static void EnsureWritableLocation(string targetPath)
    {
        var target = Path.GetFullPath(targetPath);
        if (!target.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("更新目标不是当前程序。");
        var probe = Path.Combine(Path.GetDirectoryName(target)!, ".NetPulse-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException();
            using var output = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new IOException("当前 EXE 所在位置无法写入，请将程序放在可写目录后再更新。", ex); }
    }

    public static async Task ValidatePackageAsync(string path, GitHubReleaseInfo release, CancellationToken token)
    {
        GitHubUpdateClient.Validate(release);
        await VerifyFileAsync(path, release.Size, release.Sha256, token).ConfigureAwait(false);
        await using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader?.Subsystem != Subsystem.WindowsGui ||
            !(FileVersionInfo.GetVersionInfo(path).ProductName ?? "").StartsWith("NetPulse", StringComparison.Ordinal))
            throw new InvalidDataException("新版不是有效的 NetPulse Windows x64 程序。");
        var versionText = FileVersionInfo.GetVersionInfo(path).FileVersion;
        if (!Version.TryParse(versionText, out var version) || GitHubReleaseInfo.Normalize(version).ToString(3) != release.Version)
            throw new InvalidDataException("新版 EXE 内部版本与 GitHub 发布版本不一致。");
    }

    public static async Task<SelfUpdateTransaction> PrepareAsync(string targetPath, string packagePath,
        GitHubReleaseInfo release, string dataDirectory, CancellationToken token, string[]? restartArguments = null)
    {
        var target = Path.GetFullPath(targetPath);
        if (!target.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能替换当前正在运行的单文件 EXE。");
        await ValidatePackageAsync(packagePath, release, token).ConfigureAwait(false);
        var current = FileVersionInfo.GetVersionInfo(target).FileVersion;
        if (!Version.TryParse(current, out var oldVersion) || !release.IsNewerThan(oldVersion))
            throw new InvalidOperationException("待安装版本必须高于当前版本。");
        using var parent = Process.GetCurrentProcess();
        var id = Guid.NewGuid().ToString("N");
        var request = new SelfUpdateRequest(id, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), target,
            await HashAsync(target, token).ConfigureAwait(false), new FileInfo(target).Length, parent.Id,
            parent.StartTime.ToUniversalTime().Ticks, release, Environment.CurrentDirectory, restartArguments ?? [], DateTimeOffset.UtcNow);
        var transaction = new SelfUpdateTransaction(request, Path.Combine(Path.GetFullPath(dataDirectory), "updates", id));
        Directory.CreateDirectory(transaction.DirectoryPath);
        try
        {
            // This sibling is on the destination volume, even when the app is on a USB drive.
            await CopyNewAsync(packagePath, transaction.CandidatePath, token).ConfigureAwait(false);
            await VerifyFileAsync(transaction.CandidatePath, release.Size, release.Sha256, token).ConfigureAwait(false);
            await CopyNewAsync(target, transaction.HelperPath, token).ConfigureAwait(false);
            WriteJson(transaction.RequestPath, request);
            return transaction;
        }
        catch { Cleanup(transaction); throw; }
    }

    public static SelfUpdateTransaction ReadTransaction(string requestPath, string nonce)
    {
        var full = Path.GetFullPath(requestPath);
        if (Path.GetFileName(full) != "request.json" || !File.Exists(full) || new FileInfo(full).Length > 64 * 1024)
            throw new InvalidDataException("无效的更新事务文件。");
        var request = JsonSerializer.Deserialize<SelfUpdateRequest>(File.ReadAllText(full)) ?? throw new InvalidDataException("更新事务为空。");
        if (!Guid.TryParseExact(request.Id, "N", out _) || request.Nonce == null || request.Nonce.Length != 64 || !request.Nonce.Equals(nonce, StringComparison.Ordinal) ||
            Path.GetFileName(Path.GetDirectoryName(full)) != request.Id || Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(full))) != "updates" ||
            request.TargetPath == null || !Path.IsPathFullyQualified(request.TargetPath) || Path.GetFullPath(request.TargetPath) != request.TargetPath || !request.TargetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            request.ParentId <= 0 || request.ParentStartTicks <= 0 || request.OldSize <= 0 || request.OldSha256 == null || request.OldSha256.Length != 64 || request.Release == null ||
            request.CreatedAt < DateTimeOffset.UtcNow.AddMinutes(-15) || request.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(1) ||
            request.RestartArguments == null || request.RestartArguments.Length > 16 || request.RestartArguments.Any(a => a == null || a.Length > 4096 || a.Contains('\0')))
            throw new InvalidDataException("更新事务的路径、时间或进程信息无效。");
        GitHubUpdateClient.Validate(request.Release);
        var transaction = new SelfUpdateTransaction(request, Path.GetDirectoryName(full)!);
        if (Path.GetDirectoryName(request.TargetPath)!.Equals(transaction.DirectoryPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新目标不能位于更新暂存目录中。");
        return transaction;
    }

    public static async Task StartHelperAsync(SelfUpdateTransaction transaction, CancellationToken token)
    {
        var start = new ProcessStartInfo(transaction.HelperPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(transaction.RequestPath); start.ArgumentList.Add(transaction.Request.Nonce);
        using var helper = Process.Start(start) ?? throw new IOException("无法启动更新替换进程。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (File.Exists(transaction.ReadyPath))
                {
                    var ready = ReadJson<UpdateStartupAck>(transaction.ReadyPath);
                    if (ready?.Nonce == transaction.Request.Nonce && ready.ProcessId == helper.Id) return;
                }
                if (helper.HasExited) throw new IOException(ReadJson<SelfUpdateResult>(transaction.ResultPath)?.Message ?? "更新替换进程提前退出。");
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            File.WriteAllText(transaction.AbortPath, "cancel");
            try { await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
            throw;
        }
    }

    public static async Task<int> RunHelperAsync(string requestPath, string nonce)
    {
        SelfUpdateTransaction? transaction = null;
        Process? replacement = null;
        FileStream? updateLock = null;
        var parentExited = false;
        var replaced = false;
        try
        {
            transaction = ReadTransaction(requestPath, nonce);
            var request = transaction.Request;
            if (!transaction.HelperPath.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新器必须从此事务的临时副本启动。");
            await VerifyFileAsync(transaction.HelperPath, request.OldSize, request.OldSha256, default).ConfigureAwait(false);
            using var parent = Process.GetProcessById(request.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTicks ||
                !request.TargetPath.Equals(parent.MainModule?.FileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新请求与原程序进程不匹配。");
            foreach (var instance in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(request.TargetPath)))
            {
                using (instance)
                {
                    if (instance.Id == parent.Id) continue;
                    string? image = null;
                    try { image = instance.MainModule?.FileName; }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                    if (request.TargetPath.Equals(image, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("同一 EXE 还有其他运行实例，请先关闭其他窗口再更新。");
                }
            }
            // A file lock is process-safe and can be held across async continuations.
            updateLock = new FileStream(request.TargetPath + ".netpulse-update.lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            await ValidatePackageAsync(transaction.CandidatePath, request.Release, default).ConfigureAwait(false);
            WriteJson(transaction.ReadyPath, new UpdateStartupAck(request.Nonce, Environment.ProcessId, request.Release.Version));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (!parent.HasExited)
            {
                if (File.Exists(transaction.AbortPath)) return 2;
                if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("旧程序未及时退出，已保留原 EXE。");
                await Task.Delay(100).ConfigureAwait(false);
            }
            parentExited = true;
            if (File.Exists(transaction.AbortPath)) return 2;
            await VerifyFileAsync(request.TargetPath, request.OldSize, request.OldSha256, default).ConfigureAwait(false);
            await VerifyFileAsync(transaction.CandidatePath, request.Release.Size, request.Release.Sha256, default).ConfigureAwait(false);
            await ReplaceWithRetryAsync(transaction.CandidatePath, request.TargetPath, transaction.BackupPath).ConfigureAwait(false);
            replaced = true;
            replacement = StartApplication(transaction, rolledBack: false);
            deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (true)
            {
                var ack = ReadJson<UpdateStartupAck>(transaction.AckPath);
                if (ack?.Nonce == request.Nonce && ack.ProcessId == replacement.Id && ack.Version == request.Release.Version) break;
                if (replacement.HasExited) throw new IOException("新版启动后提前退出，已尝试恢复旧版。");
                if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("新版未确认界面启动，已尝试恢复旧版。");
                await Task.Delay(100).ConfigureAwait(false);
            }
            SaveResult(transaction, "Succeeded", $"已自动更新至 v{request.Release.Version}");
            TryDelete(transaction.BackupPath);
            return 0;
        }
        catch (Exception ex)
        {
            if (transaction != null)
            {
                var message = ex.GetBaseException().Message;
                var status = "Failed";
                try
                {
                    if (replacement != null && !replacement.HasExited)
                    {
                        // Only terminate the exact child that this updater started and that failed its startup handshake.
                        replacement.Kill();
                        await replacement.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    }
                    if (replaced || File.Exists(transaction.BackupPath))
                    {
                        await VerifyFileAsync(transaction.BackupPath, transaction.Request.OldSize, transaction.Request.OldSha256, default).ConfigureAwait(false);
                        if (File.Exists(transaction.Request.TargetPath))
                            await ReplaceWithRetryAsync(transaction.BackupPath, transaction.Request.TargetPath, null).ConfigureAwait(false);
                        else File.Move(transaction.BackupPath, transaction.Request.TargetPath);
                    }
                    if (parentExited)
                    {
                        await VerifyFileAsync(transaction.Request.TargetPath, transaction.Request.OldSize, transaction.Request.OldSha256, default).ConfigureAwait(false);
                        SaveResult(transaction, "RolledBack", "更新未完成，已恢复旧版：" + message);
                        using var restored = StartApplication(transaction, rolledBack: true);
                        status = "RolledBack";
                    }
                }
                catch (Exception recovery) { message += "；恢复失败：" + recovery.GetBaseException().Message + "；旧版备份：" + transaction.BackupPath; }
                SaveResult(transaction, status, status == "RolledBack" ? "更新未完成，已恢复旧版：" + message : message);
            }
            return 1;
        }
        finally { replacement?.Dispose(); updateLock?.Dispose(); }
    }

    private static Process StartApplication(SelfUpdateTransaction transaction, bool rolledBack)
    {
        var request = transaction.Request;
        var start = new ProcessStartInfo(request.TargetPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Directory.Exists(request.WorkingDirectory) ? request.WorkingDirectory : Path.GetDirectoryName(request.TargetPath)!
        };
        foreach (var argument in request.RestartArguments) start.ArgumentList.Add(argument);
        // Keep the normal relaunch command free of temporary paths (e.g. when pinning the app).
        start.Environment["NETPULSE_UPDATE_REQUEST"] = transaction.RequestPath;
        start.Environment["NETPULSE_UPDATE_NONCE"] = request.Nonce;
        start.Environment["NETPULSE_UPDATE_ROLLBACK"] = rolledBack ? "1" : "0";
        return Process.Start(start) ?? throw new IOException("无法重新启动 NetPulse。");
    }

    public static async Task AcknowledgeStartupAsync(SelfUpdateTransaction transaction)
    {
        if (!transaction.Request.TargetPath.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新后的程序位置不匹配。");
        await ValidatePackageAsync(transaction.Request.TargetPath, transaction.Request.Release, default).ConfigureAwait(false);
        WriteJson(transaction.AckPath, new UpdateStartupAck(transaction.Request.Nonce, Environment.ProcessId, transaction.Request.Release.Version));
    }

    public static async Task CleanupAfterHelperAsync(SelfUpdateTransaction transaction)
    {
        try
        {
            var ready = ReadJson<UpdateStartupAck>(transaction.ReadyPath);
            if (ready != null)
            {
                try
                {
                    using var helper = Process.GetProcessById(ready.ProcessId);
                    if (transaction.HelperPath.Equals(helper.MainModule?.FileName, StringComparison.OrdinalIgnoreCase))
                        await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            // Retain diagnostics and backup files when recovery needs manual attention.
            var status = ReadJson<SelfUpdateResult>(transaction.ResultPath)?.Status;
            if (status == "Succeeded") TryDelete(transaction.BackupPath);
            if (status is "Succeeded" or "RolledBack") Cleanup(transaction);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public static void Cleanup(SelfUpdateTransaction transaction)
    {
        // Delete only known files; never recursively delete a computed directory.
        foreach (var path in new[] { transaction.CandidatePath, transaction.HelperPath, transaction.ReadyPath, transaction.AckPath,
            transaction.AbortPath, transaction.ResultPath, transaction.RequestPath }) TryDelete(path);
        try { if (Directory.Exists(transaction.DirectoryPath)) Directory.Delete(transaction.DirectoryPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static SelfUpdateResult? ReadLastResult(string dataDirectory) => ReadJson<SelfUpdateResult>(Path.Combine(dataDirectory, "updates", "last-result.json"));
    public static T? ReadJson<T>(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length < 64 * 1024 ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }
    private static void WriteJson<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporary, path, true);
    }
    private static void SaveResult(SelfUpdateTransaction transaction, string status, string message)
    {
        var result = new SelfUpdateResult(transaction.Request.Release.Version, transaction.Request.TargetPath, status, message, DateTimeOffset.UtcNow);
        foreach (var path in new[] { transaction.ResultPath, transaction.LastResultPath })
            try { WriteJson(path, result); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static async Task ReplaceWithRetryAsync(string source, string target, string? backup)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            try { File.Replace(source, target, backup, ignoreMetadataErrors: true); return; }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline) { await Task.Delay(200).ConfigureAwait(false); }
        }
    }
    private static async Task CopyNewAsync(string source, string destination, CancellationToken token)
    {
        await using var input = File.OpenRead(source);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
        await input.CopyToAsync(output, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }
    private static async Task VerifyFileAsync(string path, long size, string hash, CancellationToken token)
    {
        if (new FileInfo(path).Length != size || !hash.Equals(await HashAsync(path, token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新文件大小或 SHA-256 校验失败，原程序不会被替换。");
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)); }
    private static void TryDelete(string path)
    { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
}
