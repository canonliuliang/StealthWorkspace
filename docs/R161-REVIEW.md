# r16.1 产品研究与架构审查

r16.1 从公开 r16 及其发布验收提交 `859dda4` 开始。本机恢复分支为 `checkpoint/before-r16.1`。本轮按用户操作重走地址栏、标签历史、下载、离线网页、收藏迁移、工作区锁定和保险箱备份恢复。下表的用户反馈说明需求来源，项目没有代表总体的使用频率样本。

| 使用场景 | 依据与优先级 | 改动与验收 |
| --- | --- | --- |
| 从日常浏览器迁移数千条收藏 | 浏览器用户报告过换机迁移的困难。Firefox 的 HTML 导出是可复用的跨浏览器工作流。[用户提问](https://support.mozilla.org/en-US/questions/1260911) · [官方迁移说明](https://support.mozilla.org/en-US/kb/export-firefox-bookmarks-to-backup-or-transfer) | 在本机读取 HTML、规范化网页地址并一次性合并，保留主空间既有内容。使用独立 HTML 解析器，未配置网络加载器或脚本执行器。测试多行属性、中文、实体编码、重复地址、保留的原件、32 MiB 限制，以及嵌入的回环脚本和图片没有产生请求。 |
| 离开电脑时锁定工作台 | Windows 会话锁定和睡眠独立于键鼠闲置计时。已有应用将系统锁定作为独立策略。[Bitwarden 选项](https://bitwarden.com/help/vault-timeout/) · [Windows 会话消息](https://learn.microsoft.com/en-us/windows/win32/termserv/wm-wtssession-change) · [睡眠消息](https://learn.microsoft.com/en-us/windows/win32/power/pbt-apmsuspend) | 向公开 r16 的独立窗口发送锁屏通知后，WinApp UI 树仍包含地址栏和活动标签。新增可自行开启的联动，因为锁定将关闭网页。窗口消息回归覆盖关闭选项、锁屏、Windows 解锁通知和睡眠期间的待验证密码。 |
| 网页应用出错，但不希望其他网站退出登录或丢资料 | WebView2 的 HTTP 缓存、Cookie 与网页离线数据属于不同类别。[微软数据分类](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/clear-browsing-data) | 按当前顶层来源删除离线存储，测试同主机不同端口、嵌入不同顶层站点、登录 Cookie，以及删除前后的真实读回。[浏览与下载回归](R161-BROWSING.md) |
| 恢复后才发现数据不完整，或短时占用导致设置倒退 | 风险依据来自 r16 上的受控文件占用、中断、备份覆盖和恢复实验。此类故障发生频率未知，可能损失用户仅有的数据。 | 提交前检查新归档，写入时重验工作区身份，处理重复标识、截断密文和中断留下的文件。测试旧格式解密和好备份逐字节保留。[数据层对照实验](R161-DATA.md) |
| 在大型收藏中连续输入，或在临时空间看到主空间的收藏 | r16 大列表测量存在退化；收藏变更还会使全部地址身份重新计算。逐个核对建议、星标和收藏面板的读取来源，可以复现两个空间混用收藏的问题。[上一版测量](R16-PERFORMANCE.md) | 复用查询结果和书签索引，同时隔离两个空间；普通文字搜索和显式搜索运算符继续使用，特定含凭据输入或外部协议不会作为搜索词发出。[新测量与代价](R161-PERFORMANCE.md) |

应用仍采用 WinUI 3、Windows App SDK 和 Evergreen WebView2。DNS、网络连接、第三方存储分区、沙箱、GPU 与渲染器更新由运行时和 Windows 管理。此次没有变更内核开关。新增 HTML 解析仅在用户主动导入时使用，主空间设置仍使用原有 DPAPI 格式。浏览器保持单窗口，未新增扩展宿主、网络代理或遥测。

安装与发布沿用 Windows 11 x64 / ARM64 的未签名预览包。标签工作流会从 ZIP 读取内部版本与架构，把公开下载重新与标签构建原件比对，再触发官网的常规部署。[安装说明](INSTALL.md) · [发布验收记录](VALIDATION.md)
