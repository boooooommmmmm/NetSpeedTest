using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using NetPulse.Core;

namespace NetPulse;

public partial class MainWindow
{
    private UpdateStateStorage _updateStorage = null!;
    private UpdateCheckState _updateState = new();
    private CancellationTokenSource? _updateCheckCts, _updateDownloadCts;
    private readonly System.Windows.Threading.DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private Func<CancellationToken, Task<GitHubReleaseInfo?>> _releaseChecker = null!;
    private string _updateMessage = "启动后检查 GitHub 最新版本";
    private double _updateDownloadProgress;
    private bool _allowAutomaticInstallation, _updateHandoff, _updateExitRequested, _closeAfterUpdateCancel;
    public static Version CurrentVersion => GitHubReleaseInfo.Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new(1, 3, 0));
    public string VersionBadge => "●  单文件便携版 · v" + CurrentVersion.ToString(3);
    public string UpdateStatus => _updateMessage + (_updateState.LastCheckedAt is { } checkedAt ? $" · 上次检查 {checkedAt.LocalDateTime:MM-dd HH:mm}" : "");
    public bool CanCheckUpdates => !_loadingNodes && _speedCts == null && _updateCheckCts == null && _updateDownloadCts == null;
    public bool CanDownloadUpdate => CanCheckUpdates && _monitorCts == null && _updateState.Release?.IsNewerThan(CurrentVersion) == true;
    public string DownloadUpdateText => _updateHandoff ? "正在重启…" : _updateDownloadCts == null ? "更新并重启" : $"取消下载 {_updateDownloadProgress:F0}%";
    public bool UpdateDownloadButtonEnabled => !_updateHandoff && (CanDownloadUpdate || _updateDownloadCts != null);

    private void InitializeUpdates(Func<CancellationToken, Task<GitHubReleaseInfo?>>? releaseChecker)
    {
        _updateStorage = new(_nodeStorage.DirectoryPath);
        _updateState = _updateStorage.Load();
        _releaseChecker = releaseChecker ?? new GitHubUpdateClient().CheckAsync;
        AutoUpdateCheckBox.IsChecked = _updateState.Enabled;
        AutoInstallCheckBox.IsChecked = _updateState.AutoInstall;
        SetReleaseMessage();
        _updateTimer.Tick += async (_, _) => { await CheckUpdatesAsync(force: false); await TryAutoInstallAsync(); };
    }

    private void RefreshUpdateControls() => Notify(nameof(UpdateStatus), nameof(CanCheckUpdates), nameof(CanDownloadUpdate), nameof(DownloadUpdateText), nameof(UpdateDownloadButtonEnabled));
    private void SetReleaseMessage()
    {
        _updateMessage = _updateState.Release is { } release && release.IsNewerThan(CurrentVersion)
            ? $"发现新版本 v{release.Version}（{release.Size / 1048576d:F1} MiB）"
            : _updateState.LastCheckedAt == null ? "尚未检查 GitHub 最新版本" : $"当前版本 v{CurrentVersion.ToString(3)}";
        RefreshUpdateControls();
    }
    private void SaveUpdateState()
    {
        try { _updateStorage.Save(_updateState); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { _updateMessage += " · 检查记录未能保存"; }
    }
    private void AutoUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _updateState = _updateState with { Enabled = AutoUpdateCheckBox.IsChecked == true, AutoInstall = AutoInstallCheckBox.IsChecked == true };
        SaveUpdateState();
        RefreshUpdateControls();
    }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    { await CheckUpdatesAsync(force: true); await TryAutoInstallAsync(); }

    private async Task CheckUpdatesAsync(bool force)
    {
        if (_closing || !CanCheckUpdates || (!force && !_updateState.IsDue(DateTimeOffset.UtcNow))) return;
        _updateCheckCts = new();
        var token = _updateCheckCts.Token;
        _updateMessage = "正在检查 GitHub 最新版本…";
        RefreshUpdateControls();
        try
        {
            var release = await _releaseChecker(token);
            token.ThrowIfCancellationRequested();
            if (release != null) GitHubUpdateClient.Validate(release);
            // Keep a known newer release if the network returns older cached metadata.
            var retained = _updateState.Release;
            if (release == null || retained != null && Version.Parse(retained.Version) > Version.Parse(release.Version)) release = retained;
            _updateState = _updateState with { LastCheckedAt = DateTimeOffset.UtcNow, RetryAfter = null, Release = release };
            _updateMessage = release == null ? "GitHub 暂无正式发布版本"
                : release.IsNewerThan(CurrentVersion) ? $"发现新版本 v{release.Version}（{release.Size / 1048576d:F1} MiB）"
                : $"已是最新版本 v{CurrentVersion.ToString(3)}";
            SaveUpdateState();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _updateState = _updateState with { RetryAfter = DateTimeOffset.UtcNow.AddMinutes(15) };
            _updateMessage = "检查更新失败，稍后重试：" + ex.GetBaseException().Message;
            if (_updateState.Release?.IsNewerThan(CurrentVersion) == true) _updateMessage += $"；已知新版 v{_updateState.Release.Version} 仍可更新";
            SaveUpdateState();
        }
        finally
        {
            _updateCheckCts.Dispose(); _updateCheckCts = null;
            if (!_closing) RefreshUpdateControls();
        }
    }

    private void ReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        var release = _updateState.Release;
        if (release == null) { Process.Start(new ProcessStartInfo(GitHubUpdateClient.ReleasesUrl) { UseShellExecute = true }); return; }
        MessageBox.Show(this, string.IsNullOrWhiteSpace(release.Notes) ? "此版本未填写发布说明。" : release.Notes,
            $"NetPulse v{release.Version} 发布说明", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateDownloadCts != null) { if (!_updateHandoff) _updateDownloadCts.Cancel(); return; }
        await InstallUpdateAsync();
    }

    private async Task TryAutoInstallAsync(string? verifiedLocalPackage = null, string[]? restartArguments = null)
    {
        if (!_allowAutomaticInstallation || _closing || !CanDownloadUpdate ||
            !SelfUpdate.CanAutoInstall(_updateState, _monitorCts != null || _speedCts != null || _loadingNodes || _updateDownloadCts != null,
                HasSamples, DateTimeOffset.UtcNow)) return;
        await InstallUpdateAsync(verifiedLocalPackage, restartArguments);
    }

    private async Task InstallUpdateAsync(string? verifiedLocalPackage = null, string[]? restartArguments = null)
    {
        if (!CanDownloadUpdate || _updateState.Release is not { } release) return;
        var downloads = Path.Combine(_nodeStorage.DirectoryPath, "updates", "downloads");
        var packagePath = Path.Combine(downloads, Guid.NewGuid().ToString("N") + ".exe");
        SelfUpdateTransaction? transaction = null;
        var handedOff = false;
        _updateDownloadCts = new();
        var token = _updateDownloadCts.Token;
        _updateDownloadProgress = 0;
        _updateMessage = "正在下载新版并校验 SHA-256…";
        RefreshControls();
        try
        {
            SelfUpdate.EnsureWritableLocation(Environment.ProcessPath!);
            Directory.CreateDirectory(downloads);
            if (verifiedLocalPackage != null) File.Copy(verifiedLocalPackage, packagePath);
            else await new GitHubUpdateClient().DownloadAsync(release, packagePath, new Progress<double>(value =>
            {
                if (_closing || _updateDownloadCts == null) return;
                _updateDownloadProgress = value; RefreshUpdateControls();
            }), token);
            token.ThrowIfCancellationRequested();
            transaction = await SelfUpdate.PrepareAsync(Environment.ProcessPath!, packagePath, release, _nodeStorage.DirectoryPath, token, restartArguments);
            if (_samples.Count > 0)
                File.WriteAllText(transaction.SessionBackupPath, CsvExporter.Export(_samples), new System.Text.UTF8Encoding(true));
            _updateHandoff = true;
            _updateMessage = "新版校验通过，正在自动替换并重启…";
            RefreshUpdateControls();
            await SelfUpdate.StartHelperAsync(transaction, token);
            token.ThrowIfCancellationRequested();
            handedOff = true;
            _updateExitRequested = true;
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            _updateState = _updateState with { RetryAfter = DateTimeOffset.UtcNow.AddMinutes(15) };
            SaveUpdateState();
            _updateMessage = "新版下载已取消，可以重新更新。";
        }
        catch (Exception ex)
        {
            _updateState = _updateState with { RetryAfter = DateTimeOffset.UtcNow.AddMinutes(15) };
            SaveUpdateState();
            _updateMessage = "更新未完成，已保留当前程序：" + ex.Message;
        }
        finally
        {
            if (!handedOff && transaction != null)
            {
                File.WriteAllText(transaction.AbortPath, "cancel");
                SelfUpdate.Cleanup(transaction);
            }
            try { File.Delete(packagePath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _updateHandoff = false;
            _updateDownloadCts.Dispose(); _updateDownloadCts = null;
            if (!_closing) RefreshControls();
            if (_closeAfterUpdateCancel && !handedOff) Close();
        }
    }

    internal void ReportUpdateStartup(SelfUpdateTransaction transaction, bool rolledBack)
    {
        if (rolledBack)
        {
            _updateState = _updateState with { RetryAfter = DateTimeOffset.UtcNow.AddMinutes(15) };
            SaveUpdateState();
            _updateMessage = SelfUpdate.ReadLastResult(_nodeStorage.DirectoryPath)?.Message ?? "新版启动失败，已自动恢复旧版。";
        }
        else _updateMessage = $"已自动更新至 v{CurrentVersion.ToString(3)}，当前运行位置保持不变";
        if (File.Exists(transaction.SessionBackupPath)) _updateMessage += "；更新前记录已备份：" + transaction.SessionBackupPath;
        RefreshUpdateControls();
    }

    private async Task VerifyUpdateWorkflowAsync(string outputDirectory, Dictionary<string, object> report)
    {
        var fixture = new GitHubReleaseInfo("99.0.0", $"https://github.com/{GitHubUpdateClient.Repository}/releases/download/v99.0.0/NetPulse.exe",
            $"https://github.com/{GitHubUpdateClient.Repository}/releases/tag/v99.0.0", 100, new string('A', 64), "测试发布说明", DateTimeOffset.UtcNow);
        var fail = false; var calls = 0;
        Task<GitHubReleaseInfo?> Check(CancellationToken token)
        {
            calls++;
            return fail ? Task.FromException<GitHubReleaseInfo?>(new IOException("模拟 GitHub 离线")) : Task.FromResult<GitHubReleaseInfo?>(fixture);
        }
        var path = Path.Combine(outputDirectory, "update-simulation");
        var window = new MainWindow(path, automaticStartup: false, releaseChecker: Check);
        try
        {
            await window.CheckUpdatesAsync(force: true);
            if (!window.CanDownloadUpdate || !window.UpdateStatus.Contains("99.0.0") || calls != 1)
                throw new InvalidOperationException("A newer GitHub release was not shown.");
            await window.CheckUpdatesAsync(force: false);
            if (calls != 1) throw new InvalidOperationException("Periodic update check ignored the six-hour interval.");
            window._speedCts = new();
            await window.CheckUpdatesAsync(force: true);
            window._speedCts.Dispose(); window._speedCts = null;
            if (calls != 1) throw new InvalidOperationException("Update check ran during a speed test.");
            fail = true;
            await window.CheckUpdatesAsync(force: true);
            if (!window.CanDownloadUpdate || !window.UpdateStatus.Contains("失败") || calls != 2)
                throw new InvalidOperationException("A failed check discarded a previously discovered update.");
            window.AutoUpdateCheckBox.IsChecked = false;
            var reopened = new MainWindow(path, automaticStartup: false, releaseChecker: Check);
            try
            {
                if (reopened.AutoUpdateCheckBox.IsChecked != false || !reopened.CanDownloadUpdate)
                    throw new InvalidOperationException("Update preference or cached release did not survive reopening.");
                await reopened.CheckUpdatesAsync(force: false);
                if (calls != 2) throw new InvalidOperationException("Disabled automatic update check still ran.");
            }
            finally { reopened.Close(); }
            report["gitHubUpdateWorkflow"] = new { NewVersionVisible = true, CheckInterval = true, BusyDeferral = true, FailureRetention = true, PreferenceRestore = true };
        }
        finally { window.Close(); }
    }
}
