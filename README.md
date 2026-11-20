# Stealth 私密浏览器

面向 Windows 的原生浏览器与本地加密媒体工作台。基于 WinUI 3、Windows App SDK 和 Microsoft Edge WebView2。

**r16 将站点设置、清理和恢复限定在明确的范围内。** 网页会议需要麦克风时，可以只调整当前标签页的本站规则。排查网页加载问题时，可以清理 HTTP 缓存并保留网页应用的离线文件。提供 Windows 11 x64 和 ARM64 便携包。

[官网](https://canonliuliang.github.io/StealthWorkspace/) · [r16 下载与 SHA-256](https://github.com/canonliuliang/StealthWorkspace/releases/tag/v2026.11.20-r16-preview) · [安装和升级](docs/INSTALL.md) · [问题反馈](https://github.com/canonliuliang/StealthWorkspace/issues)

> 当前渠道提供未签名预览版。WebView2 会话可能写入磁盘，普通下载按明文保存。保险箱备份依赖原 Windows 用户的 DPAPI。项目尚未接受独立安全审计。

![在隔离的 Stealth r16 窗口中搜索、恢复当前会话的网页](website/assets/r16-tab-search.png)

## r16 的变化

- **临时调整本站权限。** 摄像头、麦克风、位置和通知的例外在离开来源或关闭标签时失效。允许本站自动弹窗的规则只适用于同源发起者。
- **找回刚关闭的网页。** 按 Ctrl+Shift+A，切换到「最近关闭」并输入标题或网址。只在当前会话内保留最近 20 个地址。
- **保留离线资料。** 清理 HTTP 磁盘缓存保留应用的离线内容。清除本次会话所有网站的数据前会提示哪些资料可能丢失。
- **更快筛选大量收藏。** 逐步缩小收藏筛选时会复用上一轮结果。地址栏会复用当前编辑过程中已确认的无匹配前缀。[计时、分配与退化场景](docs/R16-PERFORMANCE.md)
- **直接保存网页图片。** 在支持的图片上使用右键菜单，可直接将已加载的内容加密存入媒体库。普通下载另有显式入库操作。

## 日常使用

- **浏览**：多标签、搜索与网址识别、本地收藏/会话历史建议、标签搜索、原生查找与缩放、可恢复关闭标签。
- **下载**：跨标签共享列表，暂停、继续、取消与定位文件；关闭来源标签后仍可管理。
- **阅读图片**：网页图片阅读器与本地图片查看器，左右快速切图、长图适宽、缩放及键盘操作。
- **存放媒体**：分块 AES-GCM 加密、按需解密播放、媒体筛选与本机备份完整性检查。
- **会话与资源**：空标签按需创建内核；后台标签尝试休眠，保留表单；音频、下载和全屏页面受保护。
- **原生桌面**：深浅主题、系统动画偏好、快捷键、带图标的桌面和开始菜单入口。

## 开始使用

1. 在 Releases 选择 `win-x64`（Intel / AMD）或 `win-arm64`（Windows on Arm）。
2. 完整解压到固定目录，打开 `StealthWorkspace.exe`。请勿在 ZIP 内运行。
3. 按 [安装指南](docs/INSTALL.md) 创建快捷方式，用非重要文件先验证导入与导出。

发布包包含 .NET 与 Windows App SDK；浏览器需要 Microsoft 官方 WebView2 Runtime。未签名预览版可能触发 SmartScreen，由使用者判断是否信任，不建议关闭系统保护。

## 开发与检查

需要 Windows 和 .NET SDK 10.0.401；目标框架为 .NET 10 LTS，自包含发布包固定使用 10.0.15 运行时。

```powershell
dotnet build
dotnet run
# 打包并执行核心自检
./tools/Build-Release.ps1 -Runtime win-arm64 -Version 2026.11.20-r16-preview
# 官网本地预览
node tools/site.mjs serve
```

两个架构分别在原生 Windows runner 上构建、自检。两个公开 ZIP 均经下载校验、解压和实际启动验证。[开发说明](docs/DEVELOPMENT.md) · [r16 研究与架构](docs/R16-REVIEW.md) · [公开包与发布渠道验收](docs/VALIDATION.md) · [构建记录](https://github.com/canonliuliang/StealthWorkspace/actions/workflows/build.yml)

## 使用前应了解

锁定和加密帮助保护本地内容，但不能对抗同用户恶意程序、管理员或内核级取证。临时浏览器配置可能写盘，崩溃后清理可能延迟。普通下载不因结束会话而删除；捕获排除和文件清理受 Windows 与硬件实现限制。详见 [隐私与数据说明](docs/PRIVACY.md)。

[故障排查](docs/TROUBLESHOOTING.md) · [更新记录](CHANGELOG.md) · [安全报告](SECURITY.md) · [参与开发](CONTRIBUTING.md)

项目尚未选择开源许可证；公开可见不等于授予任意使用、修改或再分发权。第三方组件遵循各自许可证。Stealth 是独立项目，与 Microsoft 无隶属关系。
