# 开发与验证

## 环境

- Windows 11 x64 或 ARM64。
- .NET SDK 10.0.401（`global.json` 固定，允许同一 feature band 的补丁更新）。目标框架为 .NET 10 LTS + Windows SDK 26100；自包含运行时固定为 10.0.15，避免旧 SDK 默认携带旧补丁。
- PowerShell 7 用于构建和核心自检脚本；随包快捷方式脚本兼容 Windows PowerShell。
- Windows App SDK 2.6.5，由 NuGet 恢复。
- UI 验证使用 [WinApp CLI](https://github.com/microsoft/WinAppCli)；网页需要 WebView2 Runtime。

```powershell
dotnet build
dotnet run
winapp ui inspect -a StealthWorkspace --interactive
```

ARM64 构建使用 `-r win-arm64 -p:Platform=ARM64`，x64 使用 `-r win-x64 -p:Platform=x64`。

## 验证层次

`tools/Test-Core.ps1` 启动发布目录里的无界面自检，设超时，检查进程退出码以及报告中的失败计数。它覆盖加密、流式读取、密码轮换、备份恢复、地址解析、下载命名、站点数据边界和诊断日志。它不代表 XAML、网站兼容性或桌面启动已通过。

```powershell
./tools/Build-Release.ps1 -Runtime win-arm64 -Version 2026.11.14-r15-preview
./tools/Test-Core.ps1 -PublishDirectory artifacts/releases/2026.11.14-r15-preview/win-arm64/app
```

交互烟测必须使用新建的独立测试数据目录；不要把真实保险箱传给 `--data-root`，不要在真实数据上运行诊断 fixture。普通 UI 验证可使用 WinApp CLI 的 inspect / invoke / set-value / screenshot。锁定、导入、下载、切图、休眠、崩溃恢复和关闭会话需要实际交互复核。系统安全提示不应被自动绕过。

## 可复现 UI 与性能检查

`Test-UI.ps1` 总是创建独立数据目录，要求先关闭现有应用，并检查最终结果标记及进程退出码。支持 `Smoke`、`Render`、`Browser`、`Experience`、`Soak`；例如：

```powershell
./tools/Test-UI.ps1 -AppDirectory artifacts/releases/2026.11.14-r15-accepted/win-arm64/app -Mode Smoke
./tools/Test-UI.ps1 -AppDirectory artifacts/releases/2026.11.14-r15-accepted/win-arm64/app -Mode Render
./tools/Test-UI.ps1 -AppDirectory artifacts/releases/2026.11.14-r15-accepted/win-arm64/app -Mode Soak -SoakMinutes 3
./tools/Test-UI.ps1 -AppDirectory artifacts/releases/2026.11.14-r15-accepted/win-arm64/app -Theme Light -Width 640
./tools/Test-BrowserPerformance.ps1 -BaselineRoot ../StealthWorkspace-Backups/pre-overhaul-20261114/project
./tools/Measure-VaultContention.ps1 -AppDirectory artifacts/releases/2026.11.14-r15-accepted/win-arm64/app -OutputPath artifacts/contention-new.json
./tools/Test-DiagnosticIsolation.ps1 -AppDirectory artifacts/releases/2026.11.14-r15-accepted/win-arm64/app -OutputDirectory artifacts/isolation-new
```

渲染检查需要应用保持前台，使用本地合成网页。建议、锁争用及列表通知的测量不能等同于任意网页的帧率。`--ui-smoke` 还支持 `--ui-width`、`--ui-height` 和 `--ui-theme`；这些选项不会修改真实用户设置。

## 目录

| 目录 | 内容 |
| --- | --- |
| `Controls` | WebView 容器、地址栏与浏览器交互 |
| `Pages` | 浏览器、媒体、播放器、设置与清理页面 |
| `Services` | 保险箱、加密、备份、路径、日志和回归检查 |
| `Assets` | 图标与按需注入的网页图片阅读器 |
| `tools` | 构建、校验、快捷方式与本地更新工具 |
| `website` | 无框架静态官网，独立于桌面运行时 |

## 发布

提交前检查 `git diff --cached`，不要提交保险箱、会话目录、网络记录、日志转储或本机路径。自动构建在 Windows x64/ARM64 分别发布并运行核心自检。发布 ZIP 从全新的目录生成，不复用本机正在使用的 publish 文件夹；包含运行时、安装说明、快捷方式脚本、版本和 SHA-256 清单。

标签 `v*` 触发 GitHub Release 工作流，只有两个架构的构建与核心自检都成功才上传预览包。没有可信发布者证书时，Release 必须保持预览状态，并说明未签名限制。CI 通过不代表所有第三方网站可用。

官网：运行 `node tools/site.mjs build`，产物位于 `website/dist`；运行 `node tools/site.mjs serve` 在 `http://127.0.0.1:4173` 预览。`node tools/site.mjs check` 校验页面引用与必需内容。GitHub Pages 使用独立最小权限工作流。

官网部署前检查 Release 是否公开且 x64、ARM64 ZIP 与 SHA-256 清单都已上传；资料不完整时暂缓部署，保留已有官网。发布任务在 Release 创建成功后主动触发官网工作流。发布阶段仅该任务需要 `actions: write` 以发起部署；普通构建没有写权限。离线验证门控场景：`node --test tools/release-readiness.test.mjs`。
