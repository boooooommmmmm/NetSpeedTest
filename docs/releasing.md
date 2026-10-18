# 发布版本

发布到 `boooooommmmmm/NetSpeedTest`。发布脚本使用 GitHub Releases 草稿上传，公开后匿名核对附件，最后才生成客户端使用的 `release/latest.json`。

1. 修改 `src/NetPulse/NetPulse.csproj` 的三段版本号，同步 `app.manifest` 版本，编写 `docs/releases/v<版本>.md`。
2. 提交全部源码、测试和说明，推送到 `origin/main`；等待 GitHub 的 Verify 检查通过。
3. 在 Windows PowerShell 7 与 .NET 10 SDK 环境运行：

```powershell
./tools/release.ps1 -Publish
```

脚本会运行测试、构建单文件，拒绝未提交源码，核对构建前后提交未变且已推送、EXE 版本与项目一致。GitHub 凭据按 `GH_TOKEN`、`GITHUB_TOKEN`、显式指定的外部 `-TokenFile`、Git 凭据管理器顺序读取；凭据需要仓库 Contents 写权限。新增或修改 Actions 工作流的推送还需要相应工作流权限。令牌不要放在源码目录，也不要写入客户端。

脚本创建草稿 Release，上传以下附件并核对 GitHub 返回的大小与 SHA-256 digest：

- `NetPulse.exe`：给用户分发的唯一程序文件。
- `latest.json`：版本、公开地址、大小、SHA-256、说明和构建提交。
- `SHA256SUMS.txt`：供人工校验的摘要。

脚本将草稿公开，指定正式 tag `v<版本>`，重新匿名下载全部附件，核对字节哈希及 tag 的构建提交。已经公开的同版本不会被覆盖。结果保存到 `artifacts/release/v<版本>/publication.json`。

4. 脚本成功后提交并推送刚生成的 `release/latest.json`：

```powershell
git add release/latest.json
git commit -m "Publish version manifest for v<版本>"
git push
```

5. 验证实际客户端接口和下载：

```powershell
dotnet run --project tests/NetPulse.Tests/NetPulse.Tests.csproj -c Release -- --verify-github
```

该检查验证公开 GitHub Release、旧版本客户端能识别升级、当前版本显示已是最新，并用客户端的实际下载逻辑重新下载 EXE，核对大小及 SHA-256。官方 raw 清单也应与公开 Release 附件一致；GitHub 缓存延迟时等待并复查实际地址，避免重发公开附件。

客户端只连接固定官方仓库，发布检查与下载使用 HTTPS。SHA-256 验证下载完整性，信任来源为 GitHub 官方 HTTPS 元数据；它不是独立发布者签名，也不是 Windows Authenticode 代码签名。

v1.5 起支持同一路径替换与自动重启。后续版本须保持 `--apply-update`、`NETPULSE_UPDATE_REQUEST` / `NETPULSE_UPDATE_NONCE` / `NETPULSE_UPDATE_ROLLBACK` 和启动确认协议兼容，并在发布前运行 `pwsh -NoProfile -File tools/verify-self-update.ps1`。重启上下文只传递给新进程并立即清除，不写入常规启动命令；也兼容显式完成/回滚参数。该工具在隔离目录构建旧版本夹具和新版，验证真实文件替换、空闲自动安装、启动失败回滚、损坏包拒绝安装、只读目标保护、设置与记录备份；GitHub Verify 也运行它。v1.4 及更早版本仍需手动升级到 v1.5 一次。
