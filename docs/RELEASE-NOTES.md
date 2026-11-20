Stealth r16 更新了日常浏览中几个容易误操作的地方。现在可以只为当前网页调整权限、在本次会话中找回特定标签，也可以清理缓存并保留网页应用的离线内容。

### 权限与清理

- 在站点隐私面板中，可以临时修改当前标签页里当前来源的摄像头、麦克风、位置和通知规则。换到其他来源或关闭标签后，临时例外消失。跨来源内嵌网页继续使用默认规则。选择「所有网站」才会保存新的默认设置。
- 清理 HTTP 磁盘缓存会保留 Cookie 和网页应用的离线存储。清除所有网站的 Cookie 或本地存储前，应用会说明操作范围并要求确认。
- 结束会话清理临时浏览目录时，遇到文件占用会留下所有权标记。后续启动可重试清理带标记、超过 24 小时且没有活动所有者的旧目录。完成清理前，临时数据仍可能留在磁盘上。

### 标签、收藏与图片

- 按 **Ctrl+Shift+A**，按标题或网址查找标签，切换到「最近关闭」可以挑选本次会话最近关闭的 20 个网页地址。恢复会重新打开网址。锁定或结束会话会清空记录，未提交的表单内容不会进入恢复记录。
- 收藏夹支持按名称或网址在本机筛选，并可重命名、删除条目。筛选打开的标签时复用列表快照，减少输入过程中重复读取内核状态和创建图标的工作。
- 在网页图片的右键菜单或图片阅读器中，可以把已加载的受支持图片直接加密存入媒体库。此操作省去在下载夹中写入明文副本的步骤。单张上限 32 MiB，普通下载仍保存为普通文件。

### 本地检索实测

在一台 Windows 11 ARM64 虚拟机上，50,000 条合成收藏中连续输入 21 个没有匹配的前缀，两次运行的中位耗时从约 9.9 ms 降到 1.0–1.1 ms。命中列表末尾的另一组 14 次输入从约 0.88 ms 增至约 1.82 ms。锁屏启动差异落在样本波动内。[测试方法和完整结果](https://github.com/canonliuliang/StealthWorkspace/blob/v2026.11.20-r16-preview/docs/R16-PERFORMANCE.md)

### 下载与升级

支持目标为 Windows 11。Intel / AMD 电脑选择 `Stealth-2026.11.20-r16-preview-win-x64.zip`，Windows on Arm 选择 `Stealth-2026.11.20-r16-preview-win-arm64.zip`。运行浏览器需要 Microsoft Edge WebView2 Runtime。

升级前备份重要数据并退出旧版，核对同一 Release 的 `SHA256SUMS.txt`，将新 ZIP 完整解压到新的固定目录，然后打开 `StealthWorkspace.exe`。旧程序旁如有 `WorkspaceData`，按安装指南迁移完整目录并保留原件。发布包未签名，Windows 可能显示 SmartScreen。备份依赖原 Windows 用户；换机前请另外保存重要原始文件。[官网下载](https://canonliuliang.github.io/StealthWorkspace/#download) · [安装与升级指南](https://github.com/canonliuliang/StealthWorkspace/blob/v2026.11.20-r16-preview/docs/INSTALL.md)

临时会话仍使用本机的网络连接。网站和网络服务可以看到访问流量或 IP 地址，WebView2 和 Windows 也有各自的后台行为。[隐私与数据说明](https://github.com/canonliuliang/StealthWorkspace/blob/v2026.11.20-r16-preview/docs/PRIVACY.md) · [源代码](https://github.com/canonliuliang/StealthWorkspace/tree/v2026.11.20-r16-preview)

### 公开包验证

GitHub 于 2026 年 11 月 20 日 16:15:44 UTC 发布本版。公开的 ARM64 和 x64 ZIP 均经匿名下载，与标签构建的原件和 SHA-256 清单一致。两份解压包均在 Windows 11 ARM64 上实际启动，各完成 40 次标签打开、加载和关闭，正常退出并清理临时会话。x64 界面测试使用系统兼容层，x64 构建和核心自检另在原生 x64 runner 上通过。[校验清单](https://github.com/canonliuliang/StealthWorkspace/releases/download/v2026.11.20-r16-preview/SHA256SUMS.txt) · [发布验收记录](https://github.com/canonliuliang/StealthWorkspace/blob/main/docs/VALIDATION.md) · [更新公告](https://liu-digital-space.pages.dev/updates/#stealth-r16-preview)
