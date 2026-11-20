# r16.1 产品研究与架构审查

r16.1 从公开 r16 及其发布验收提交 `859dda4` 开始。本机恢复分支为 `checkpoint/before-r16.1`。本轮按用户操作重走地址栏、标签历史、下载、离线网页、收藏迁移、工作区锁定和保险箱备份恢复。下表的用户反馈说明需求来源，项目没有代表总体的使用频率样本。

| 使用场景 | 依据与优先级 | 改动与验收 |
| --- | --- | --- |
| 从日常浏览器迁移数千条收藏 | 浏览器用户报告过换机迁移的困难。Firefox 的 HTML 导出是可复用的跨浏览器工作流。[用户提问](https://support.mozilla.org/en-US/questions/1260911) · [官方迁移说明](https://support.mozilla.org/en-US/kb/export-firefox-bookmarks-to-backup-or-transfer) | 在本机读取 HTML、规范化网页地址并一次性合并，保留主空间既有内容。使用独立 HTML 解析器，未配置网络加载器或脚本执行器。测试多行属性、中文、实体编码、重复地址、保留的原件、32 MiB 限制，以及嵌入的回环脚本和图片没有产生请求。 |
| 离开电脑时锁定工作台 | Windows 会话锁定和睡眠独立于键鼠闲置计时。已有应用将系统锁定作为独立策略。[Bitwarden 选项](https://bitwarden.com/help/vault-timeout/) · [Windows 会话消息](https://learn.microsoft.com/en-us/windows/win32/termserv/wm-wtssession-change) · [睡眠消息](https://learn.microsoft.com/en-us/windows/win32/power/pbt-apmsuspend) | 向公开 r16 的独立窗口发送锁屏通知后，WinApp UI 树仍包含地址栏和活动标签。新增可自行开启的联动，因为锁定将关闭网页。窗口消息回归覆盖关闭选项、锁屏、Windows 解锁通知和睡眠期间的待验证密码。 |
| 网页应用出错，但不希望其他网站退出登录或丢资料 | WebView2 的 HTTP 缓存、Cookie 与网页离线数据属于不同类别。[微软数据分类](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/clear-browsing-data) | 按当前顶层来源删除离线存储，测试同主机不同端口、嵌入不同顶层站点、登录 Cookie，以及删除前后的真实读回。[浏览与下载回归](R161-BROWSING.md) |
| 恢复后才发现数据不完整，或短时占用导致设置倒退 | 风险依据来自 r16 上的受控文件占用、中断、备份覆盖和恢复实验。此类故障发生频率未知，可能损失用户仅有的数据。 | 提交前检查新归档，写入时重验工作区身份，处理重复标识、截断密文和中断留下的文件。测试旧格式解密和好备份逐字节保留。[数据层对照实验](R161-DATA.md) |
| 在大型收藏中连续输入，或在临时空间看到主空间的收藏 | r16 大列表测量存在退化；收藏变更还会使全部地址身份重新计算。逐个核对建议、星标和收藏面板的读取来源，可以复现两个空间混用收藏的问题。[上一版测量](R16-PERFORMANCE.md) | 复用查询结果和书签索引，同时隔离两个空间；普通文字搜索和显式搜索运算符继续使用，特定含凭据输入或外部协议不会作为搜索词发出。[新测量与代价](R161-PERFORMANCE.md) |

分享链接时附带的广告归因字段会被传给下一次访问的网站。Firefox 已提供主动清理后复制的操作。这里同样由用户主动触发，只修改明确列出的查询字段，保留其余字段的原始字节与片段。凭据、可识别的签名、授权以及支付链接触发提示。[Firefox 120 发布说明](https://www.firefox.com/en-US/firefox/120.0/releasenotes/) · [Windows 剪贴板可选项](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.datatransfer.clipboard.setcontentwithoptions)

应用仍采用 WinUI 3、Windows App SDK 和 Evergreen WebView2。DNS、网络连接、第三方存储分区、沙箱、GPU 与渲染器更新由运行时和 Windows 管理。此次没有变更内核开关。新增 HTML 解析仅在用户主动导入时使用，主空间设置仍使用原有 DPAPI 格式。浏览器保持单窗口，未新增扩展宿主、网络代理或遥测。

旧版工作区在锁页和禁用闲置锁定时仍每 5 秒唤醒 UI 定时器。新逻辑按本机输入时间计算下一次可能到期的时刻，锁定或关闭闲置策略时停止定时器。修改超时和从睡眠恢复时重新计算。任务栏重新显示窗口后通过原生可见性事件恢复快捷隐藏时的静音。回归核对计算边界与 Windows 32 位计时回绕，未从定时器数量推算整机续航。

安装与发布沿用 Windows 11 x64 / ARM64 的未签名预览包。标签工作流会从 ZIP 读取内部版本与架构，把公开下载重新与标签构建原件比对，再触发官网的常规部署。[安装说明](INSTALL.md) · [发布验收记录](VALIDATION.md)

## 用户反馈与验收边界

有用户在换浏览器时发现时间戳或文件夹层级丢失。r16.1 的收藏编辑器仍使用平面列表，设置页在选择文件之前说明这一点；支持的 HTML 加入日期可往返。另有 Brave 用户报告单站清理失效后只能全局删除。本次把同主机不同端口、跨顶层嵌入和登录 Cookie 一同放入测试，防止小范围修复误删其他网页数据。两例都是公开个案，缺少据以估算总体发生率的样本。[收藏迁移用户反馈](https://www.reddit.com/r/browsers/comments/1p6qlu8/how_to_import_bookmarks_from_html_file_to_chrome/) · [Brave 单站清理问题](https://github.com/brave/brave-browser/issues/40098)

| 审查范围 | 实现与验收范围 |
| --- | --- |
| 内核、进程和图形 | WinUI 负责地址、标签和窗口。WebView2 在独立进程处理文档与 GPU 合成，共用本次会话的环境。检查了首次建核、进程失效后恢复、后台标签尝试休眠和渲染回归。 |
| 请求、缓存与沙箱 | 宿主在清理时区分 HTTP 缓存、离线存储和 Cookie。DNS 解析、连接复用、渲染沙箱和浏览器运行时更新由 WebView2 和 Windows 管理。保留运行时机制，实测网页访问以及不同来源的数据边界。 |
| 隐私、权限与扩展 | 同一会话内的标签共享浏览器配置。当前来源、临时例外和全局选项有各自作用范围。未提供扩展安装或密码自动填充。退出时检查临时会话目录；操作系统和外部站点仍可保留记录。 |
| 持久化与并发 | Windows 用户 DPAPI 保护设置、收藏和库配置，媒体使用分块加密。通过文件占用、锁定、空间切换、并行下载、旧备份恢复和中断恢复检验写入顺序。 |
| 构建与更新 | 两个 Windows 原生 runner 分别构建便携包并自检；标签发布复核归档内程序集、架构与提交以及公开校验清单。应用手动升级，WebView2 独立更新。 |

WebView2 进程模型和 Evergreen 运行时的更新边界采用微软说明。[进程模型](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-model) · [运行时分发与更新](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)
