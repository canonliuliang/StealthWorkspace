# Stealth R16.1 第二次预览

本版供 Windows 11 x64 和 ARM64 使用。支持从 Chrome 导入分层收藏，在新标签页或网页的收藏栏直接打开具体链接。

## 收藏与浏览

- 在 Chrome 书签管理器的菜单中选择「导出书签」，然后在 Stealth 新标签页选择「导入 Chrome 收藏」。网站、具体视频和图片链接可分别放进文件夹。导入后保留嵌套层级，可继续新建文件夹、移动或重命名收藏。
- 首页与网页可分别放大、缩小和重置。网页的「更多」菜单提供页内查找、当前会话历史和下载目录，空白首页只显示当下可用的操作。
- 在主空间关闭会话后，Stealth 清理本地浏览记录和临时内容，保留网站用于持续登录的数据。在地址栏的盾牌中可以清除一个网站或全部网站的登录。网站主动退出、登录到期或只使用会话 Cookie 时，仍可能需要重新登录。临时空间单独运行。
- 从首页、更多菜单或下载列表打开「下载文件夹」，可查看以往的实际文件、切换已有位置，并将所选文件复制到加密媒体库。普通下载保留明文原件。若 Windows 禁止应用打开资源管理器，仍可在应用内查看文件和选择目录路径。
- 清理后显示已处理、被占用和权限受限等结果。可从清理页打开 Windows 自身的存储设置。媒体库修复了删除重试时误处理其他待删文件的问题，也修复了加密动画 WebP 只显示第一帧的问题。
- 修复了在媒体库、图片和锁屏之间来回切换时偶发的程序退出。已有保险箱打开锁屏时，只会加载解锁所需的控件。

## 安装与升级

先备份重要数据并正常退出旧程序。Intel / AMD 电脑选 `win-x64`，Windows on Arm 选 `win-arm64`。对照下方的 SHA256SUMS.txt 核对 ZIP，完整解压后运行 StealthWorkspace.exe。在新目录运行随包提供的 Install-Shortcuts.ps1，可更新桌面和开始菜单图标的目标。使用便携保险箱时请按安装指南保留旧的 WorkspaceData。预览包未使用受信任的发布者证书签名，Windows 可能显示发布者确认。[安装与更新](https://github.com/canonliuliang/StealthWorkspace/blob/v2026.11.21-r16.1-preview.2/docs/INSTALL.md) · [隐私说明](https://github.com/canonliuliang/StealthWorkspace/blob/v2026.11.21-r16.1-preview.2/docs/PRIVACY.md) · [上一版发布记录](https://github.com/canonliuliang/StealthWorkspace/releases/tag/v2026.11.21-r16.1-preview)
