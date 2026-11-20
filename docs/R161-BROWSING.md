# r16.1 浏览、下载和站点数据验收

## 为什么改

Firefox 用户曾询问怎样迅速找到访问过的网页，也有用户需要修复单一网站的显示故障。Firefox 的历史搜索及按站点清理说明给出了可比较的工作流。Stealth r16 的历史面板只能浏览最近 200 条记录，存储面板只提供全网站清理。访问较多页面或使用多个离线网页应用时，这些限制可以直接复现。公开反馈不足以估算发生率。[历史搜索需求](https://support.mozilla.org/en-US/questions/1274245) · [单一网站故障反馈](https://support.mozilla.org/en-US/questions/1390202) · [Firefox 历史](https://support.mozilla.org/en-US/kb/browsing-history-firefox) · [Firefox 按站点清理](https://support.mozilla.org/en-US/kb/clear-cookies-and-site-data-firefox)

## 用户可见行为

- **本站的离线数据。** 隐私面板的「存储」先列出本站选项。确认框显示协议、主机和端口，说明未同步资料的影响。确认后只请求清理这个第一方来源的 localStorage、IndexedDB 和 CacheStorage。Cookie 可以继续使用。其他端口、其他来源及第三方分区独立。仍在运行的网站可以重新创建数据。切换文档、撤销或失去前台会使待确认请求失效。运行时未能执行此命令时会显示错误，不扩大清理范围。[Chromium 存储接口](https://chromedevtools.github.io/devtools-protocol/tot/Storage/#method-clearDataForOrigin) · [Chromium 第一方存储键实现](https://chromium.googlesource.com/chromium/src/+/e8ea048d0fd135923d5dbaa06bb779e6a6bb2a9f/content/browser/devtools/protocol/storage_handler.cc)
- **查找当前会话的页面。** Ctrl+H 打开历史搜索。可以按标题和网址筛选、按 Enter 打开第一条匹配。无匹配时有单独提示。离开历史面板会放弃搜索词，关闭面板后解除筛选监听。查询在已有的会话内存记录中执行。
- **批量关闭。** 快捷键 Ctrl+Shift+W 和「结束会话」使用同一条确认路径，即使仅有一个活动网页也能取消。右键「关闭其他标签页」遇到已加载页面会显示数量，提醒表单和下载的处理方式。确认应用于打开提示时列出的标签。锁定和隐私遮罩仍可立即接管窗口。[Firefox 的多标签关闭设置](https://support.mozilla.org/en-US/kb/how-configure-close-tab-warnings-firefox)
- **地址和键盘。** 从网页转到空白文档会更新地址栏、标题和收藏状态。内嵌 data 文档只把协议写入地址输入框和标签搜索的地址字段。快捷键识别保留 Ctrl+Alt 和 Windows 徽标组合，减少将 AltGr 的文本输入识别为收藏或查找命令的情况。[Windows 国际键盘指南](https://learn.microsoft.com/en-us/windows/win32/uxguide/inter-keyboard)
- **下载。** 取消操作显示「下载已取消」。命名处理识别上标数字形式的 Windows 设备名、控制台设备名和方向格式控制字符。截断长文件名时保留完整 Unicode 字符和扩展名。普通中文、阿拉伯语及希伯来语名称仍可使用。[Windows 文件命名规则](https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file) · [WebView2 下载状态](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2downloadoperation)

## 现场验证

Windows 11 ARM64，WebView2 157.0.4341.60，独立数据目录。浏览器回归采用真实 WebView2 和绑定到回环地址的 HTTP 站点。

| 场景 | 检查 |
| --- | --- |
| 清理单站 | 先在同主机的两个端口分别创建数据库、离线缓存和本地草稿。另在 localhost 顶层页面中嵌入第一站，为嵌入分区写入不同值。清理第一站后，三项数据消失；Cookie、另一端口的三项内容和嵌入分区的值均保留。 |
| 查找与标签 | 已访问网页能按网址和标题筛选，空查询结果有明确提示，关闭面板后检索状态释放。HTTP 网页转到 about:blank 后不再显示旧地址和收藏状态。两个已加载标签取消批量关闭后均保留。 |
| 下载 | 同一名称在一个已完成文件及两个并行下载间取得三个不同路径。在来源标签关闭后取消其中一个已暂停的下载，原文件和另一份 4 MiB 完成文件逐字节一致；活动下载计数回到零，隐藏控制器释放。 |
| 文件名 | 对公开 r16 源码和本版源码运行同一组 6 个输入。r16 保留了三个 Windows 特殊名和一个方向覆盖符，另一个长名截出了不完整的 UTF-16。r16.1 在五种情形修正输出，两版均保留普通希伯来文件名。 |

从项目根目录运行 `./tools/Test-UI.ps1 -AppDirectory <解压包目录> -Mode Browser` 和 `-Mode Smoke`。前者会在报告旁写入真实界面的历史搜索和存储面板截图。下载回归还包含暂停、继续、内容核对和浏览器进程恢复。这里没有按云服务登录账号、各语言实体键盘或所有浏览器数据类型测试。单一标签关闭时，网页未提交的表单不会进入「最近关闭」记录。

独立分支的 Smoke 与 Browser 均有完整通过记录。一轮截图暴露了搜索框未正确取得焦点，修正后确认了键盘焦点、实际可见的两个匹配行并重新截图。该轮后续的浏览器崩溃注入在旧测试时限内没有观察到退出，因此这次整轮记为失败。回归现分别验证原生进程退出和应用恢复通知，以区分注入未生效与恢复失败。正式包的结果以[发布验收](VALIDATION.md)为准。

## 原生文件选择器与锁定

导出收藏时，Windows 原生「另存为」及其警告框是工作区窗口所拥有的独立窗口。修复前，发送 Windows 锁屏通知后，两层对话框仍出现在界面树中，退出等待正在选择文件的任务超过 15 秒。现在文件选择操作在弹出前登记，锁定、遮罩和退出均撤销该操作。异步选择器的调用方可先结束等待，系统稍后返回的文件路径会被丢弃。[微软文件选择器说明](https://learn.microsoft.com/en-us/windows/apps/develop/files/) · [Win32 所有者窗口](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features)

六个入口迁移至 Windows App SDK：媒体库的多文件导入、单文件导出、多文件导出至文件夹，设置中的备份创建、备份检查，以及锁定页的备份恢复。它们与两项收藏 HTML 导入导出操作共用当前工作区的 WindowId。备份保留 `.swbackup` 过滤条件，媒体导出保留下载目录和原文件名，媒体多选保留 Win32 后备。Windows 消息只发送给同进程且所有者链回到工作区、包含文件选择器 Shell 视图的对话框及其警告。在所有权隔离测试中，目标对话框及警告关闭，同一工作区的其他对话框、另一根窗口及其选择器均保留。[微软文件选择器设计](https://github.com/microsoft/WindowsAppSDK/blob/main/specs/Storage.Pickers/Microsoft.Windows.Storage.Pickers.md)

Windows 11 ARM64 的独立桌面实测使用真实系统选择器和独立库。正常确认时，单选完整返回一个测试文件的路径，多选完整返回指定的两个文件路径，文件夹选择返回指定目录，三次均释放任务且库保持解锁。发送 Windows 会话锁定通知后，打开单文件、多文件、文件夹和 Win32 多选窗口分别在 59、57、64、73 毫秒时观察为不可见，四次均返回空结果并释放任务。另存为窗口和嵌套的覆盖确认框同时可见时，锁定后 59 毫秒观察到两层均已消失。相同场景直接关闭主窗口后，57 毫秒观察到两个窗口均已消失，114 毫秒后进程正常退出。两次已有文件均保留原内容。数值为该设备单次轮询观测值。Windows 构建、Shell 扩展或运行权限不同可能影响系统界面，此轮使用普通用户权限。实际发布包复测范围见[发布验收](VALIDATION.md)。
