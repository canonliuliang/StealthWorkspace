# 安装与更新

Stealth 是 Windows 桌面应用，当前提供预览版本。建议使用 Windows 11；Windows 10 的兼容性尚未完成实机验证。它不是 Microsoft 产品。

## 选择版本

- Intel / AMD 电脑：`win-x64`。
- Windows on Arm（例如 Snapdragon 或 Apple Silicon 上的 Windows 虚拟机）：`win-arm64`。
- 在 Windows「设置 → 系统 → 系统信息 → 系统类型」确认。不要根据浏览器显示的架构猜测。

从仓库 Releases 下载 ZIP，完整解压到固定且可写的目录，然后打开 `StealthWorkspace.exe`。不要只复制 EXE，也不要直接在 ZIP 中运行。发布包包含 .NET 和 Windows App SDK；网页需要 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。

当前版本为 `2026.11.20-r16-preview`。便携包使用手动更新；WebView2 Evergreen 运行时由 Microsoft 独立维护。[下载 r16 和 SHA-256 清单](https://github.com/canonliuliang/StealthWorkspace/releases/tag/v2026.11.20-r16-preview)

当前预览包未使用受信任发布者证书签名，Windows 可能显示 SmartScreen 提示。校验值只能检查下载内容是否一致，不能代替发布者签名。由使用者判断是否信任；不要关闭系统保护。参见 [Microsoft 应用声誉说明](https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation)。

## 快捷方式

固定解压目录后，在 PowerShell 运行随包提供的脚本：

```powershell
./Install-Shortcuts.ps1 -PublishDirectory .
```

它创建当前用户的桌面与开始菜单入口，无需管理员权限。若系统策略不允许运行脚本，可在资源管理器中为 `StealthWorkspace.exe` 手动创建快捷方式。不要移动已创建快捷方式指向的程序目录；移动后需重新创建。

## 第一次使用

1. 创建保险箱密码并妥善保管。应用不提供云端找回密码。
2. 解锁进入浏览器。地址栏可直接输入网址或搜索内容；输入时的收藏/历史建议在本地生成。
3. 在媒体库导入一份不重要的测试文件，先确认能够打开、导出与备份，再导入重要内容。
4. 在设置中创建加密备份并检查完整性。当前备份依赖原 Windows 用户的 DPAPI，重装系统或换机后可能无法恢复，不是跨电脑备份。重要原始文件应另行妥善备份。

## 更新与卸载

更新前创建并检查加密备份，然后正常退出应用。将新版本解压到另一固定目录；使用本机数据目录的安装通常会沿用原数据。

如果旧程序旁存在 `WorkspaceData`，它可能就是实际保险箱：在程序已退出时，将**整个目录**复制到新程序旁，保留原副本，再启动新版本验证。新目录已经有 `WorkspaceData` 时不要合并或覆盖，应先确认两边分别是什么数据。不要只复制配置文件，也不要因为新版本显示空库就删除旧目录。

新安装通常使用 `%LOCALAPPDATA%\StealthWorkspace\Data`；已有安装可能沿用旧目录或程序旁 `WorkspaceData`。详见 [隐私与数据说明](PRIVACY.md)。程序不会在卸载时自动替你删除这些数据。删除前确认备份可恢复。

## 校验下载

```powershell
Get-FileHash .\Stealth-*-win-arm64.zip -Algorithm SHA256
```

把结果与同一 Release 的 `SHA256SUMS.txt` 对比。文件名、架构与版本必须对应。
