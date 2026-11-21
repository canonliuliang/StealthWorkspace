# Stealth R16.1 便携目录维护更新

此版本为 `2026.11.21-r16.1-preview.2.1`，支持 Windows 11 x64 和 ARM64。

「更多 → 在 Edge 隐私窗口打开」在新标签页和已打开的网页都可见。从首页可打开独立的 InPrivate 窗口，从网页可将当前网址交给 Edge。在 Windows 以受限权限运行 Stealth 时，菜单会提示如何从 Edge 打开 InPrivate，并提供复制当前网址的按钮。其他网页操作也保留在菜单中，首页会说明何时可用。结束会话回到标签栏。地址栏和顶部的隐私入口使用蓝青色盾牌，随系统对比度调整。点击地址栏盾牌可只清 Cookie，也可清除网站的账号和离线文件。

如果便携保险箱放在很多层文件夹里，之前可能会遇到重置未完成、部分媒体无法移除，或退出后临时下载目录仍存在。这些问题在关闭 Windows 长路径支持的电脑上可以复现。本次修订调整应用管理自身目录的方式。安装更新不需要改动 Windows 策略。

主空间继续默认保留网站登录，退出时清理本地访问记录、下载记录和网页临时缓存。地址栏盾牌可清除一个网站或所有网站的账号。收藏栏继续支持从 Chrome 导入分层书签。网站主动退出、撤销凭据或账号过期后仍需重新登录。[日常浏览与验证](https://github.com/canonliuliang/StealthWorkspace/blob/v2026.11.21-r16.1-preview.2.1/docs/R161-DAILY-BROWSING.md) · [上一个公开版本](https://github.com/canonliuliang/StealthWorkspace/releases/tag/v2026.11.21-r16.1-preview.2)

特别深的网站资料目录或很长的站点主机名可能导致 WebView2 无法保存部分网页的离线草稿。已记录本机验证范围与处理方法。[网页离线资料故障排查](https://github.com/canonliuliang/StealthWorkspace/blob/v2026.11.21-r16.1-preview.2.1/docs/TROUBLESHOOTING.md)

先备份重要资料，正常关闭 Stealth。Intel / AMD 电脑下载 x64，Windows on Arm 下载 ARM64。用同一发行页的 SHA256SUMS.txt 校验 ZIP，在旧安装位置完整更新程序文件并保留原有 WorkspaceData。可以从原位置运行 Install-Shortcuts.ps1 更新同名桌面图标。预览包未使用受信任的发布者证书签名。[安装指南](https://github.com/canonliuliang/StealthWorkspace/blob/v2026.11.21-r16.1-preview.2.1/docs/INSTALL.md)
