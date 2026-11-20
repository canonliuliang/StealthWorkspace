Stealth r16.1 为 Windows 11 x64 和 ARM64 提供未签名便携预览包。本版重点是换浏览器时带走收藏、修复单个网站，以及在文件被占用或操作中断时保住原有数据。

### 浏览与收藏

- 在设置中按 **Ctrl+F** 搜索「收藏」，选择其他浏览器导出的 HTML。导入合并同一网页，保留已有收藏和输入顺序。主空间可导出明文 HTML，诱饵空间使用独立的临时收藏。
- 按 **Ctrl+H**，按标题或网址查找本次会话的历史。回车打开匹配项。结束会话快捷键也会要求确认。
- 排查某个网页应用时，可单独清理当前来源的本地存储、IndexedDB 和离线缓存，保留 Cookie。页面跳到空白文档时，地址栏会同步更新。
- 「更多操作 → 复制净化链接」可移除已知跟踪参数，保留页面所需的其他参数及片段。遇到识别出的登录、支付、签名和令牌链接会提示。
- 改进下载名称处理、同名下载及关闭来源标签后的验证。普通下载仍是普通文件。
- 地址栏阻止特定含凭据的裸地址和已识别的外部协议被误当作搜索词。地址建议和星标只使用当前空间的收藏。

### 保护正在使用的数据

- 在设置中可以选择跟随 Windows 锁屏、远程断开或睡眠来锁定工作台。开启会结束网页会话，请先保存表单。首次启动显示锁页时保留当前剪贴板。新安装默认保留整机 DNS 缓存。
- 锁屏或退出会收回尚未完成的文件选择，包括覆盖确认框。闲置锁定按截止时间唤醒，在锁页或禁用时停止计时。
- 设置文件短时不可读时，应用保留原件并提示只读。新保险箱备份先写入临时文件，回读核对摘要、媒体文件头和索引后，再替换已有备份。
- 恢复中断后可复用内容一致的文件，遇到冲突保留原文件。公开 r16 创建的保险箱已验证能在同一 Windows 用户下恢复。跨机使用前请另存重要原始文件。[数据验证](https://github.com/canonliuliang/StealthWorkspace/blob/v2026.11.21-r16.1-preview/docs/R161-DATA.md)

### 实测与升级

在 Windows 11 ARM64 虚拟机上，5 万条合成收藏中重命名末条并刷新星标，两次独立运行的中位耗时由约 97–98 ms 降到约 56–57 ms。收藏保存的磁盘阶段现在在后台执行。首次建立索引的部分样本仍有退化。[测试环境、方法与完整数据](https://github.com/canonliuliang/StealthWorkspace/blob/v2026.11.21-r16.1-preview/docs/R161-PERFORMANCE.md)

Intel / AMD 电脑选择 win-x64，Windows on Arm 选择 win-arm64。升级前备份重要数据并退出旧版。核对本页的 SHA256SUMS.txt，将 ZIP 完整解压到新的固定目录后运行 StealthWorkspace.exe。WebView2 Runtime 由微软独立维护，应用使用手动升级。[官网下载](https://canonliuliang.github.io/StealthWorkspace/#download) · [安装与更新](https://github.com/canonliuliang/StealthWorkspace/blob/v2026.11.21-r16.1-preview/docs/INSTALL.md) · [隐私说明](https://github.com/canonliuliang/StealthWorkspace/blob/v2026.11.21-r16.1-preview/docs/PRIVACY.md)
