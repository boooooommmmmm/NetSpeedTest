# v1.3.0 发布验收

- 正式发布：[NetPulse v1.3.0](https://github.com/boooooommmmmm/NetSpeedTest/releases/tag/v1.3.0)。
- 构建源码提交：`41dde304bba1184d08803db3c8b83e2744a641aa`，正式 tag 指向该提交。
- [该提交的 GitHub Verify](https://github.com/boooooommmmmm/NetSpeedTest/actions/runs/39103726739)通过，26 项核心测试全部通过。
- EXE：`NetPulse.exe`，Windows x64 自包含单文件，文件版本 `1.3.0.0`，发布版本 `1.3.0`。
- 文件大小：`64,977,955` 字节。
- SHA-256：`e0553c2fa0342a0cabdd21058dc8401ff144cf75fb4b7419ea245efab9607661`。

正式发布前核对了草稿附件的 GitHub digest；公开后匿名重新下载 `NetPulse.exe`、`latest.json`、`SHA256SUMS.txt`，逐一与本地原件核对 SHA-256。公开 EXE 的字节与本地已构建产物一致。

本地 EXE 冒烟测试覆盖 GitHub 新版本提示、六小时间隔、测速期间顺延、失败保留已知新版、更新设置恢复；同时验证测速网节点/IP 同步和真实下载、上传。当前升级入口为检查与手动保存新 EXE，用户关闭旧版后运行新文件。

清单在公开附件验证完成后单独提交；客户端实际入口：

- GitHub Releases API：`https://api.github.com/repos/boooooommmmmm/NetSpeedTest/releases/latest`。
- 官方 raw 回退：`https://raw.githubusercontent.com/boooooommmmmm/NetSpeedTest/main/release/latest.json`。

可运行 `dotnet run --project tests/NetPulse.Tests/NetPulse.Tests.csproj -c Release -- --verify-github` 重新验证公开版本、旧版本号识别及客户端真实 EXE 下载校验。
