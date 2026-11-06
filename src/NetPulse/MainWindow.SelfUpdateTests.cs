using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using NetPulse.Core;

namespace NetPulse;

public partial class MainWindow
{
    // These explicit modes run only against isolated copies supplied by tools/verify-self-update.ps1.
    internal async Task RunSelfUpdateScenarioAsync(string outputDirectory, string packagePath, string mode)
    {
        try
        {
            if (!Path.GetFullPath(Environment.ProcessPath!).StartsWith(Path.GetFullPath(outputDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Self-update verification requires an isolated executable copy.");
            AutoSyncBox.IsChecked = false; SyncIntervalBox.SelectedIndex = 2; ProxyBox.IsChecked = true;
            AutoUpdateCheckBox.IsChecked = false; AutoInstallCheckBox.IsChecked = false;
            AddSample(new(DateTimeOffset.UtcNow, "update-session-marker", ProbeMode.Tcp, TrafficPhase.Idle, 12.5, "record backup fixture"));
            var version = GitHubReleaseInfo.Normalize(Version.Parse(FileVersionInfo.GetVersionInfo(packagePath).FileVersion!)).ToString(3);
            using var package = File.OpenRead(packagePath);
            var release = new GitHubReleaseInfo(version, $"https://github.com/{GitHubUpdateClient.Repository}/releases/download/v{version}/NetPulse.exe",
                $"https://github.com/{GitHubUpdateClient.Repository}/releases/tag/v{version}", package.Length,
                Convert.ToHexString(await SHA256.HashDataAsync(package)), "Self-update integration fixture", DateTimeOffset.UtcNow);
            package.Close();
            _updateState = _updateState with { Release = release };
            SaveUpdateState();
            if (mode == "competing-update")
            {
                using var competingLock = new FileStream(Environment.ProcessPath + ".netpulse-update.lock",
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                await InstallUpdateAsync(packagePath);
                if (_updateExitRequested || _updateDownloadCts != null || _updateHandoff || !UpdateStatus.Contains("更新未完成"))
                    throw new InvalidOperationException("A conflicting update did not leave the current application usable.");
                WriteSelfUpdateProof(outputDirectory, "RejectedBeforeExit", null);
                Close();
                return;
            }
            if (mode == "helper-start-failure")
            {
                var transaction = await SelfUpdate.PrepareAsync(Environment.ProcessPath!, packagePath, release, _nodeStorage.DirectoryPath, default);
                try
                {
                    // Fail validation inside the real helper and make its abort marker unwritable.
                    await File.AppendAllTextAsync(transaction.CandidatePath, "corruption");
                    Directory.CreateDirectory(transaction.AbortPath);
                    try
                    {
                        await SelfUpdate.StartHelperAsync(transaction, default);
                        throw new InvalidOperationException("Corrupt helper candidate was accepted.");
                    }
                    catch (IOException ex) when (ex.Message.Contains("校验")) { }
                }
                finally
                {
                    Directory.Delete(transaction.AbortPath);
                    SelfUpdate.Cleanup(transaction);
                }
                if (File.Exists(transaction.HelperPath)) throw new InvalidOperationException("Failed helper was still running during cleanup.");
                WriteSelfUpdateProof(outputDirectory, "RejectedBeforeExit", null);
                Close();
                return;
            }
            if (mode == "readonly-target")
            {
                var target = Environment.ProcessPath!;
                var attributes = File.GetAttributes(target);
                try { File.SetAttributes(target, attributes | FileAttributes.ReadOnly); await InstallUpdateAsync(packagePath); }
                finally { File.SetAttributes(target, attributes); }
                if (!UpdateStatus.Contains("无法写入")) throw new InvalidOperationException("Read-only executable was not rejected before handoff.");
                WriteSelfUpdateProof(outputDirectory, "RejectedBeforeExit", null);
                Close();
                return;
            }
            if (mode == "auto-idle")
            {
                AutoUpdateCheckBox.IsChecked = true; AutoInstallCheckBox.IsChecked = true;
                _allowAutomaticInstallation = true;
                await TryAutoInstallAsync(packagePath);
                if (_updateExitRequested) throw new InvalidOperationException("Automatic update interrupted existing records.");
                ResetSamples();
                _speedCts = new();
                await TryAutoInstallAsync(packagePath);
                _speedCts.Dispose(); _speedCts = null;
                if (_updateExitRequested) throw new InvalidOperationException("Automatic update interrupted a speed test.");
                await TryAutoInstallAsync(packagePath, ["--self-update-restarted", outputDirectory, mode]);
                if (!_updateExitRequested) throw new InvalidOperationException("Idle automatic installation did not start: " + UpdateStatus);
                return;
            }
            if (mode == "corrupt-package")
            {
                var corruptPath = Path.Combine(outputDirectory, "corrupt.exe");
                File.Copy(packagePath, corruptPath, true);
                await File.AppendAllTextAsync(corruptPath, "corruption");
                await InstallUpdateAsync(corruptPath);
                if (!UpdateStatus.Contains("校验")) throw new InvalidOperationException("A corrupt update was not rejected.");
                WriteSelfUpdateProof(outputDirectory, "RejectedBeforeExit", null);
                Close();
                return;
            }
            await InstallUpdateAsync(packagePath, ["--self-update-restarted", outputDirectory, mode]);
            if (!_updateExitRequested) throw new InvalidOperationException("Updater handoff failed: " + UpdateStatus);
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(Path.Combine(outputDirectory, "test-error.txt"), ex.ToString());
            Environment.ExitCode = 1;
            Close();
        }
    }

    internal void WriteSelfUpdateProof(string outputDirectory, string status, SelfUpdateTransaction? transaction)
    {
        var settings = _nodeStorage.LoadSettings();
        var backup = transaction?.SessionBackupPath;
        var auto = transaction?.Request.RestartArguments.Contains("auto-idle") == true;
        if (status == "RolledBack" && !(_updateState.RetryAfter > DateTimeOffset.UtcNow))
            throw new InvalidOperationException("Rollback lost its in-memory retry cooldown.");
        File.WriteAllText(Path.Combine(outputDirectory, "proof.json"), JsonSerializer.Serialize(new
        {
            Status = status, Version = CurrentVersion.ToString(3), Path = Environment.ProcessPath,
            SettingsPreserved = !settings.Enabled && settings.IntervalMinutes == 30 && settings.UseProxy,
            PreferencesPreserved = _updateState.Enabled == auto && _updateState.AutoInstall == auto,
            RecordsBackedUp = backup != null && File.Exists(backup) && File.ReadAllText(backup).Contains("update-session-marker"),
            RollbackCooldown = _updateState.RetryAfter > DateTimeOffset.UtcNow,
            SessionBackup = backup, UpdateStatus
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
