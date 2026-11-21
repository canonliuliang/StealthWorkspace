# 安装与更新

Stealth 是 Windows 桌面应用，当前提供预览版本。建议使用 Windows 11；Windows 10 的兼容性尚未完成实机验证。它不是 Microsoft 产品。

## 选择版本

- Intel / AMD 电脑：`win-x64`。
- Windows on Arm（例如 Snapdragon 或 Apple Silicon 上的 Windows 虚拟机）：`win-arm64`。
- 在 Windows「设置 → 系统 → 系统信息 → 系统类型」确认。不要根据浏览器显示的架构猜测。

从仓库 Releases 下载 ZIP，完整解压到固定且可写的目录，然后打开 `StealthWorkspace.exe`。不要只复制 EXE，也不要直接在 ZIP 中运行。发布包包含 .NET 和 Windows App SDK；网页需要 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。

当前版本为 `2026.11.21-r16.1-preview.2`。便携包使用手动更新；WebView2 Evergreen 运行时由 Microsoft 独立维护。[下载 R16.1 第二次预览和 SHA-256 清单](https://github.com/canonliuliang/StealthWorkspace/releases/tag/v2026.11.21-r16.1-preview.2)

当前预览包未使用受信任发布者证书签名，Windows 可能显示 SmartScreen 提示。校验值只能检查下载内容是否一致，不能代替发布者签名。由使用者判断是否信任；不要关闭系统保护。参见 [Microsoft 应用声誉说明](https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation)。

## 快捷方式

固定解压目录后，在 PowerShell 运行随包提供的脚本：

```powershell
./Install-Shortcuts.ps1 -PublishDirectory .
```

脚本在当前用户桌面和开始菜单创建 Stealth 图标。更新时从新目录再次运行，已有的 Stealth 快捷方式会指向新版。若系统策略不允许运行脚本，可在资源管理器中为 `StealthWorkspace.exe` 手动创建快捷方式。不要移动已创建快捷方式指向的程序目录；移动后需重新创建。

## 第一次使用

1. 创建保险箱密码并妥善保管。应用不提供云端找回密码。
2. 解锁进入浏览器。地址栏可直接输入网址或搜索内容；输入时的收藏/历史建议在本地生成。
3. 在媒体库导入一份不重要的测试文件，先确认能够打开、导出与备份，再导入重要内容。
4. 在设置中创建加密备份并检查完整性。当前备份依赖原 Windows 用户的 DPAPI，重装系统或换机后可能无法恢复，不是跨电脑备份。重要原始文件应另行妥善备份。

## 更新与卸载

同一个 Windows 用户继续使用原位置的同一保险箱时，R16.1 第二次预览会沿用自己保存的网站登录。换电脑、还原备份，或把便携保险箱移到另一个目录后，可能需要重新登录网站。第一次从旧预览升级时，旧版已经清除的登录也需要重新建立一次。

从其他浏览器迁入收藏时，先在原浏览器将书签导出为 HTML，再在主空间首页选「从 Chrome 导入」，也可在设置中搜索「收藏」。支持最多 32 MiB 的单个文件和 HTTP(S) 网页地址。收藏栏会保留文件夹层级，同一文件夹内的相同网址只添加一次。导出 HTML 可用于转移收藏，不能用于恢复保险箱。

希望沿用桌面图标、原保险箱和本机网站登录时，请使用原来的安装位置。

1. 在应用中创建并检查加密备份，正常退出应用，等窗口关闭。
2. 在资源管理器中右键旧桌面图标，选「打开文件所在的位置」。保留该目录的完整备份，尤其是其中可能存在的 `WorkspaceData`。
3. 把已核验的新 ZIP 完整解压到一个空目录。确认能看到 `StealthWorkspace.exe`，再将该目录内的全部文件和子目录复制到旧安装目录，允许替换同名程序文件。不要先清空旧目录。发布包不包含保险箱或个人配置。
4. 从更新后的原安装目录运行 `Install-Shortcuts.ps1`，然后从桌面打开 Stealth。原有快捷方式会更新；缺失的图标会重新创建。若程序无法打开，先退出，再从备份恢复旧程序文件。

如果要把软件迁到另一固定目录，使用本机数据目录的安装通常会沿用原保险箱。网站账号资料与保险箱位置有关，迁移后可能需要再次登录。

如果旧程序旁存在 `WorkspaceData`，它可能就是实际保险箱：在程序已退出时，将**整个目录**复制到新程序旁，保留原副本，再启动新版本验证。新目录已经有 `WorkspaceData` 时不要合并或覆盖，应先确认两边分别是什么数据。不要只复制配置文件，也不要因为新版本显示空库就删除旧目录。

新安装通常使用 `%LOCALAPPDATA%\StealthWorkspace\Data`；已有安装可能沿用旧目录或程序旁 `WorkspaceData`。详见 [隐私与数据说明](PRIVACY.md)。程序不会在卸载时自动替你删除这些数据。删除前确认备份可恢复。

## 校验下载

```powershell
Get-FileHash .\Stealth-*-win-arm64.zip -Algorithm SHA256
```

把结果与同一 Release 的 `SHA256SUMS.txt` 对比。文件名、架构与版本必须对应。
