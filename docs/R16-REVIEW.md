# r16 产品研究、架构和验收

版本为 `2026.11.20-r16-preview`。接手时已有站点权限、会话恢复、媒体和动效的在途改动。审查前本地恢复点为 `e2aa248`，上一个公开发布标签为 `v2026.11.14-r15-preview`。本轮把这些改动与后续修复放到同一发布候选中验证。

## 用户需求与取舍

仓库缺少能代表用户总体的反馈样本，项目没有采集使用频率的遥测。下表中的触发条件可复现，社区反馈只说明这些工作流确有使用者。

| 工作流和依据 | 工程发现、选择与验收 |
| --- | --- |
| 网页会议、隐私敏感用户需要临时改变权限。WebView2 使用者报告过拒绝后重新申请权限困难。[WebView2 用户反馈](https://github.com/MicrosoftEdge/WebView2Feedback/issues/2889) · [Firefox 的标签级权限](https://firefox-source-docs.mozilla.org/permissions/browser-scoped.html) | 面板明确区分本站与全局，按协议、主机和端口匹配。弹窗例外也检查发起帧。验收包括跨源 iframe、跨端口、离开后返回和正常 POST 弹窗。 |
| 多标签用户希望直接找到已开标签，超过一次关闭后还希望选回特定页面。社区同时关心清理后能否继续恢复。[Zen 用户讨论](https://github.com/zen-browser/desktop/discussions/807) · [Mozilla 用户对清理的反馈](https://connect.mozilla.org/t5/discussions/recently-closed-tabs-are-not-cleared/td-p/118815) | Ctrl+Shift+A 提供两个列表，最近关闭最多保留 20 个地址。选定条目后重新导航，保留用户未关闭的其他标签。为降低暴露，恢复记录随锁定、会话结束或历史清理失效。 |
| 研究人员与知识工作者查找大量收藏时会遇到输入延迟。[Firefox 地址栏反馈](https://bugzilla.mozilla.org/show_bug.cgi?id=1279864) · [大收藏夹个案](https://bugzilla.mozilla.org/show_bug.cgi?id=1986910) | 在 200、10,000、50,000 条收藏下测量。选择复用不可变快照与已知匹配，避免常驻另一份完整检索索引。对所有匹配结果和顺序做等价检查；部分完整扫描变慢。 |
| 网页应用使用 Cache API 和 IndexedDB 保存离线内容。清理缓存或排查登录时，使用者需要知道会影响什么。[WebView2 数据分类](https://github.com/MicrosoftEdge/WebView2Feedback/blob/main/specs/ClearBrowsingData.md) · [离线 PWA 工程指南](https://learn.microsoft.com/en-us/microsoft-edge/progressive-web-apps/how-to/offline) | 原 HTTP 清理路径还删除 CacheStorage。现在只清除 DiskCache。用本机 WebView2 验证脚本再次请求，同时能读回离线原文、草稿和 Cookie。跨网站清理数据需另行确认。 |
| 下载和图片阅读用户希望合上来源标签后保留任务，并能区分明文下载与入库。WebView2 开发者还报告过释放用户目录后文件仍被占用。[目录占用讨论](https://github.com/MicrosoftEdge/WebView2Feedback/discussions/4479) | 只保留交付下载事件所需的空白控制器，验收暂停、继续及逐字节对比。直接图片入库使用已加载资源。文件占用时保留临时目录标记，释放后重试。 |

## 浏览器边界

| 层 | 本次核对 |
| --- | --- |
| 窗口、标签与进程 | WinUI 3、Windows App SDK 2.6.5、.NET 10。单应用窗口，共用一个 WebView2 环境及会话目录。空标签按需创建控制器。网络、渲染、GPU 合成和站点隔离由 Evergreen WebView2 管理。 |
| 响应与内存 | 页内 DOM 可用后，符合条件的后台标签调用原生休眠；前台、音频、下载与全屏另作处理。实测休眠后表单恢复。启动基准止于锁屏就绪。渲染测试使用本地合成页面。 |
| 网络与指纹 | 使用运行时的 DNS、连接复用、缓存、平衡跟踪防护和 SmartScreen。应用不提供独立 VPN、DNS 转发器、指纹随机化或第三方 Cookie 全量拦截策略。未对 WebRTC、DNS、各站点存储分区作跨网络覆盖测试。[Edge 跟踪防护](https://learn.microsoft.com/en-us/microsoft-edge/web-platform/tracking-prevention) |
| 身份与扩展 | 搜索建议来自本机收藏和会话历史，用户提交后才访问搜索引擎。应用关闭密码自动保存与通用自动填充。没有用户扩展管理入口。临时配置独立于媒体库的加密。WebView2 与 Windows 仍可能进行自己的诊断通信。[WebView2 数据与隐私](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/data-privacy) |
| 数据与生命周期 | 会话历史和最近关闭列表在内存中。锁定时终止会话。普通下载留在应用下载目录。媒体格式为分块 AES-GCM，设置和备份受当前 Windows 用户 DPAPI 约束。崩溃后允许重建内核，未落盘 Cookie 或未提交表单可能丢失。 |
| 构建与更新 | Windows 11 x64 / ARM64 的自包含未签名 ZIP，手动更新应用。运行时使用 Evergreen 更新渠道。发布工作流在相应原生 runner 上先构建和自检，官网等到两份公开 ZIP 与校验清单齐备后部署。[运行时分发](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution) |

## 验收证据

本机为 Windows 11 Pro ARM64（26300），Parallels 虚拟机，4 vCPU、约 6 GiB 内存。使用隔离目录和合成保险箱。ARM64 原生及 x64 兼容层候选 ZIP 均实际解压、逐文件校验，两架构各 232 项核心检查通过，0 项失败，2 项 SUBST 环境跳过。NuGet 的已知漏洞数据源在本次直接与传递依赖扫描中未报告命中。

实机操作打开 MDN 和 Firefox 帮助页面，关闭两个标签、打开最近关闭列表，以 Firefox 筛选后回车恢复指定网页。核对了全会话存储清理的取消入口。浏览器自动回归覆盖真实 WebView2 的离线数据、站点 Cookie、正常弹窗、全屏、休眠、下载、渲染器和浏览器进程恢复。前两次前台验收因 Windows 拒绝激活而失败；显式激活隔离窗口后重新执行。生产行为和断言均保留。

更多测量、复现命令与验证范围见[性能记录](R16-PERFORMANCE.md)、[隐私验证](R16-PRIVACY-TESTS.md)和[发布验收记录](VALIDATION.md)。本轮未做独立安全审计、Windows 10 实机、完整商用账号登录/支付流程、物理设备续航或长期跨站点兼容性测试。

ARM64 解压包的真实浏览、减少动态效果下的图片和隐私面板、渲染走查均通过。本机 48 张合成图片页面交错测试中，应用 WebView 的两次帧间隔 P95 为 18.4 / 18.1 ms，同机裸 WebView2 为 18.1 / 18.0 ms，各窗口采集 179 帧。实测验证渲染路径，缺少据此判断两版滚动速度变化的依据。
