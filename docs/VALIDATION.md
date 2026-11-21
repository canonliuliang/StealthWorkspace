# 验证范围

## R16.1 第二次预览维护更新

**2026-11-21 08:40:13 UTC** 公开 Windows ARM64 和 x64 未签名便携预览包。维护标签指向合并提交 `f079ca2dc9dd64fc292350c5876688fe1a16230f`。官网、个人站软件下载页、更新公告和 RSS 均已核对为本次版本。[GitHub Release](https://github.com/canonliuliang/StealthWorkspace/releases/tag/v2026.11.21-r16.1-preview.2.1) · [维护 PR #10](https://github.com/canonliuliang/StealthWorkspace/pull/10) · [官网](https://canonliuliang.github.io/StealthWorkspace/) · [软件页](https://liu-digital-space.pages.dev/work/stealthworkspace/) · [公告](https://liu-digital-space.pages.dev/updates/#stealth-r16-1-preview-2-1) · [RSS](https://liu-digital-space.pages.dev/feed.xml)

### 公开下载与校验

| 从官网链接匿名完整下载的文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| Windows ARM64 ZIP | 96,301,008 | `c697349abe39f4baca2f3eaeb6c0a163f6f671887585543764a72f0f84e2c256` |
| Windows x64 ZIP | 98,678,691 | `0097927aab240d203d3f29d549f690113eab7d9854b9521019f445d97145ad3a` |
| SHA256SUMS.txt | 232 | `40dd3aef7b225a5a7cdcbf890f232dd92815b1ceb401496caf27e10dc639a5a8` |

两个 ZIP 均与标签流水线的原始包逐字节相同。解压后的 ARM64 547 个文件、x64 551 个文件逐个核对 SHA-256，通过检查。程序集版本包含本次标签的完整提交。原生 Windows ARM64 运行器通过 321 项、失败 0 项、跳过 1 项，原生 x64 运行器通过 319 项、失败 0 项、跳过 3 项。两台运行器均没有可用的 SUBST 映射，x64 测试卷另有两项缺少独立的 8.3 短文件名。[ARM64 构建日志](https://github.com/canonliuliang/StealthWorkspace/actions/runs/44885663559/job/136790272805) · [x64 构建日志](https://github.com/canonliuliang/StealthWorkspace/actions/runs/44885663559/job/136790273257) · [发布后官网部署](https://github.com/canonliuliang/StealthWorkspace/actions/runs/44885962556)

从公开页面重新下载、解压后，ARM64 在 79 和 158 字符的合成数据父目录各跑一轮核心检查，x64 在 77 和 158 字符的目录各跑一轮。四轮均为 320 项通过、0 项失败、2 项盘符映射检查因环境受限跳过。

从公开 ARM64 包重新启动 11 个独立应用进程，另观测到 13 个 WebView2 浏览器进程，Windows 实测完整性等级均为 Low（RID 4096）。两个站点由服务器核对 HttpOnly 登录状态。正常锁定和重启后登录仍有效，普通 GET 的服务端计数首次为 0→1→1，重启后为 1→2→2，站点离线 CacheStorage 仍在。强制退出后的原生历史表保留了 2 条测试访问。恢复后的检查显示旧访问 0 条、新访问 2 条，再次正常退出后本批访问为 0 条。关闭最后一个网页、保留空标签再退出，也核对了磁盘访问记录为零。单站及全站数据清除、退出登录、临时空间和另一保险箱的隔离均用合成数据验证。[账号、缓存与历史的重跑方法](R161-DAILY-BROWSING.md)

### 本机验收

使用 Windows 11 ARM64 26300.9974、200% 显示缩放和 WebView2 157.0.4341.60。测试使用合成保险箱、回环网站与下载文件。x64 界面在这台机器上通过 Windows on Arm 仿真运行。

- **Cookie 与网站账号。** 在两个合成网站分别写入登录 Cookie、localStorage、IndexedDB 和 CacheStorage，并由服务端核对登录。取消清理后两站均能登录。确认「仅清除本站 Cookie」只让当前站登出，确认「仅清除所有 Cookie」让两站登出。三种网站存储在这些操作后仍可读取。
- **下载、菜单与结束会话。** 下载 4 MiB 文件，清空下载列表后从更多菜单打开下载文件夹，复制进保险箱并逐字节核对。从目录的原生关闭按钮返回后，再从工具栏下载列表打开同一目录，两次均能看到磁盘文件。普通权限和受限权限各完整执行一次。取消结束会话保留原有标签和网址，确认后只留一个可以继续打开网页的新标签，并完成历史、下载记录和临时缓存清理。常规权限下新 Edge InPrivate 窗口实际访问了测试网址。受限权限下验证了指引、取消和复制被拒后的全选重试，复制请求的内容及剪贴板历史、漫游选项由隔离写入端核对，未向个人系统剪贴板写入。
- **后台标签。** 在真实 Windows 前台测试窗口，普通左键选择子页，Ctrl 单击和中键单击保留当前页。旧版在链接处理约 1 秒或中键按住约 1.15 秒时会抢走当前标签。修复后，慢速页面上的 Ctrl 单击、中键单击和长按中键都在后台打开。网页取消了中键动作后再按 Enter（包括鼠标刚放开的紧接按键）、普通单击、跳转到新文档或页面稍后自行打开窗口，也核对了实际选中的标签。
- **隐私入口和图库。** 标题栏和地址栏的盾牌在原生浅色、深色窗口实拍，第三组在单个窗口预览 Windows 对比色资源，测试机未开启整机高对比度。两轮常规动效及一轮减少动效均完整通过下载通知中断重开、加密图片快速切换、锁定清理和删除至空图库。800 × 12000 长图的最后一次切换以阅读区域的实际像素核对。三轮均实际显示 WebP 的 1、2、3、1、2、3 帧，最终第三帧分别持续确认 2749、2547 和 2742 毫秒。专供原生截图观测的三帧时长是 900、1200 和 1500 毫秒，帧图像、混合方式和两次循环与旧夹具相同。
- **应用自管深目录。** 在 Windows 长路径策略关闭时，上一公开包在 154 字符的隔离数据父目录有 5 项失败。相同深度的修复候选在 ARM64 及仿真 x64 都为 318 通过、0 失败、2 项盘符映射检查因环境受限而跳过。345 字符的密文路径实测完成导入、重新解锁、导出、删除和重置。人工占用刚复制的一个文件 110 毫秒，修复前连续 3 次旧库迁移失败，修复后 3 次都完成迁移并逐项核对 32 个文件及重置标志。长期占用仍会明确失败。

**WebView2 网站数据库的限制。** 一次浏览回归使用 162 字符的诊断根，WebView2 实际用户数据目录达 197 字符，IndexedDB 建库失败。相同候选改为 96 字符的诊断根后全套浏览通过。普通安装在本机使用 96 字符的固定账户目录，其位置由 Windows LocalLow 和固定长度的保险箱标识决定。相同目录下，50 与 80 字符的合成回环主机能使用 IndexedDB，100 字符合成主机建库失败。同期 Cookie、localStorage、CacheStorage 均可用。使用扩展路径访问同一目录也未改变结果。此结果来自上述 Windows 与内核版本，实际网站发生率未知。保险箱备份不含网站账号目录。[安装与更新](INSTALL.md) · [隐私与数据边界](PRIVACY.md)

### 启动与多标签负载

在 4 个逻辑处理器、约 6 GiB 内存的 Parallels ARM 虚拟机上，对上一公开 ARM64 包和本次最终候选各预热一次，再交错运行 7 对。每次用全新合成资料，二进制文件缓存已预热。计时从操作系统创建进程到 WinUI 锁页根控件首次加载，浏览器内核和首个网页尚未启动。

| 进程到锁页 | 中位数 | 最短–最长 |
| --- | ---: | ---: |
| 上一公开包 R16.1 第二次预览 | 841.2 ms | 733.6–855.4 ms |
| 本次维护候选 | 848.6 ms | 544.3–855.1 ms |

两组范围重叠，七对样本不足以支持启动提速结论。连续三分钟反复打开、关闭标签，共完成 12 轮，保留标签的页面状态和退出清理均通过。宿主私有内存采样峰值为 74.7 MiB，网页渲染进程另计。[启动测试工具](../tools/Measure-BrowserStartup.ps1)

## R16.1 第二次预览

GitHub 于 **2026-11-21 06:29:31 UTC** 公开 Windows ARM64 和 x64 未签名便携预览包。标签 [`v2026.11.21-r16.1-preview.2`](https://github.com/canonliuliang/StealthWorkspace/releases/tag/v2026.11.21-r16.1-preview.2) 指向 [`374789f`](https://github.com/canonliuliang/StealthWorkspace/commit/374789f8ce52951748051737cf41f497a578336f)，对应[功能与修复 PR #8](https://github.com/canonliuliang/StealthWorkspace/pull/8)。[标签构建与发布](https://github.com/canonliuliang/StealthWorkspace/actions/runs/44875495179) · [main 双架构构建](https://github.com/canonliuliang/StealthWorkspace/actions/runs/44875488460) · [发布后官网部署](https://github.com/canonliuliang/StealthWorkspace/actions/runs/44875784778)

| 从 Release 完整下载的资产 | 字节 | SHA-256 |
| --- | ---: | --- |
| Windows ARM64 ZIP | 96,274,317 | `5896455bcf7065e454227a6f35d2340d1739bc111be92eee9c168dd5c77c6a11` |
| Windows x64 ZIP | 98,651,962 | `f361a752deed629d0a5eba060d167fdfe038c700333067e4d70e7ea720363498` |
| SHA256SUMS.txt | 228 | `138db154a1c6002a430357daeca6e6f496a103cf824cfda80770ae8ea14763b5` |

从官网的三个下载链接匿名完整下载 ZIP 和清单。两包的 SHA-256 均与公开清单、GitHub 资产摘要及标签构建中的原始 ZIP 相符。解压后 ARM64 有 547 个文件、x64 有 551 个文件。主程序集的嵌入版本和提交均与本次标签一致。[公开下载与校验清单](https://github.com/canonliuliang/StealthWorkspace/releases/tag/v2026.11.21-r16.1-preview.2)

标签流水线在原生 Windows 运行器完成核心检查。ARM64 通过 314 项、失败 0 项、跳过 1 项，x64 通过 312 项、失败 0 项、跳过 3 项。两架构各有一项缺少现存 SUBST 盘符映射。x64 测试卷另有两项无法产生独立的 8.3 短文件名。[ARM64 构建记录](https://github.com/canonliuliang/StealthWorkspace/actions/runs/44875495179/job/136759174963) · [x64 构建记录](https://github.com/canonliuliang/StealthWorkspace/actions/runs/44875495179/job/136759174749)

- **公开 ARM64 包的账号与历史。** 从官网下载后，11 个独立应用进程和观测到的 13 个 WebView2 子进程都以 Windows 低完整性等级（RID 4096）运行，11 个阶段全部通过。两个回环网站验证了 HttpOnly 登录 Cookie、localStorage、IndexedDB 和 CacheStorage。正常关窗或锁定后主空间仍能登录。用相同地址发起请求，正常关窗再启动后服务器收到新的请求。清除一个站点后另一站仍能登录。强制终止时磁盘仍有两条测试访问，下次使用浏览器前清理，再次正常关窗后原生历史表中的本批访问为零。网站可自行令账号过期，已登录的服务也可保留观看历史。[数据、用例与重跑方法](R161-DAILY-BROWSING.md)
- **收藏、下载与菜单。** 从全新 Chrome 配置真正导出的 HTML 经系统文件选择器导入，得到 3 条链接和 5 个文件夹。沿收藏栏打开多级目录中的视频链接，核对了目标路径。下载 4 MiB 合成文件后清空任务列表，仍能从应用内目录找到文件并导入保险箱。正常权限下资源管理器打开正确目录。受限权限下验证了应用内目录。网页缩放以实际像素比核对，首页缩放以原生控件倍率核对。[操作路径与性能数据](R161-DAILY-BROWSING.md)
- **媒体与锁定。** 旧候选的 3 个专项进程中有 2 个在快速返回图库时触发原生崩溃。修复后 3 个独立进程完成 72 次锁定、解锁和返回图库，其中 36 次立即切页、36 次按已加载的图库按钮。另有 6 轮完整操作、两种首次密码设置，以及 WAV、MP4 播放和备份还原。12 个进程均正常退出。动画 WebP 在真实图片控件上显示了三种预期画面，播放两轮后停止。

本机使用 Windows 11 ARM64（系统构建 26300），x64 界面通过 Windows on Arm 仿真运行。两架构最终本地包各有 313 项核心检查通过，0 项失败，另有 2 项 SUBST 别名检查因环境受限而跳过。一项缺少对应现成盘符映射，另一项被系统拒绝创建临时盘符。上述账号和媒体测试均使用合成资料。

**深目录仍有限制。** 在 Windows 长路径支持关闭（`LongPathsEnabled=0`）的测试机上，将公开 ARM64 包的合成测试数据置于 154 个字符的诊断父目录，核心检查第一次得到 308 通过、5 失败、2 跳过。未通过的五项是旧下载清理与新会话隔离、重置旧库后重载或迁移、重置标记写入失败时保留内容、清理联接目录并保护外部目标，以及媒体库重命名、批量导出和部分删除重试的组合检查。同一批文件改用 70 个字符的诊断父目录时，ARM64 和 x64 各为 313 通过、0 失败、2 跳过。既有保险箱先备份并校验，便携安装可选较短的固定目录。[安装与更新](INSTALL.md)

两版 ARM64 各预热后交错测量 7 次，从创建进程到锁页首次完成加载。第一次预览为 354.9 ms 中位数（347.4–372.0 ms），本次为 357.8 ms（351.0–369.9 ms）。这是热文件缓存下的启动，未创建首个网页。另用三分钟反复开关 12 个标签，三个保留标签的页面状态通过检查。宿主私有内存采样峰值为 72.2 MiB，渲染进程另计。

## R16.1 第一次预览

GitHub 于 **2026-11-20 17:59:04 UTC（UTC+8 为 11 月 21 日 01:59:04）**发布 Windows x64 / ARM64 未签名便携预览版。[公开下载与清单](https://github.com/canonliuliang/StealthWorkspace/releases/tag/v2026.11.21-r16.1-preview) · [标签原生构建、校验和发布](https://github.com/canonliuliang/StealthWorkspace/actions/runs/44787842133) · [官网生产部署](https://github.com/canonliuliang/StealthWorkspace/actions/runs/44788449507)

| 公开资源 | 字节 | SHA-256 |
| --- | ---: | --- |
| Windows ARM64 ZIP | 96,175,587 | `37e56a19427ac671d0ee95d232cfbc1dafbcba0bc8a872b33718aefe2674d356` |
| Windows x64 ZIP | 98,553,233 | `24f6190c86f828b317e9f89cc241e75086e6cdd3e5106c4b92a34f19e4063d5e` |
| SHA256SUMS.txt | 224 | `57c5e0b4af3e654eea8420caad431caacc2dde135b744ca1110c50d952fd8d5a` |

在 Windows 11 ARM64 上使用隔离测试目录，实测 HTML 收藏导入（2 条新增、1 条重复、1 条本地路径跳过），再用系统「另存为」导出并读回原网址与日期。向实际 WinUI 窗口发送 Windows 会话锁定通知后，已打开的原生保存窗口及嵌套覆盖确认在 59 ms 内被收回。直接关闭时两层窗口在 57 ms 内消失，进程在 114 ms 内退出，既有目标文件内容均保留。此项没有锁定整个测试设备。

[浏览、下载与原生对话框](R161-BROWSING.md) · [旧版数据迁移与恢复](R161-DATA.md) · [性能方法和原始区间](R161-PERFORMANCE.md) · [审查范围与用户研究](R161-REVIEW.md)

[PR #7](https://github.com/canonliuliang/StealthWorkspace/pull/7) 合入提交 [ddf36e7](https://github.com/canonliuliang/StealthWorkspace/commit/ddf36e7b870f609888526f7c934f38a5efd44ddd)，标签为 `v2026.11.21-r16.1-preview`。本机最终候选的 x64 和 ARM64 便携包均为 281 项核心检查通过、0 失败、2 项 SUBST 环境跳过。ARM64 原生、x64 在 Windows on Arm 兼容层实际启动，两份 Smoke 均完成 40 次打开、加载和关闭标签，设置查找、锁定撤销与退出清理均通过。第一轮设置检索脚本过早读取 XAML；改为等待真实 TextChanged 事件后，两架构重新全程通过。[PR 的双架构原生构建](https://github.com/canonliuliang/StealthWorkspace/actions/runs/44787188899)

候选包的 Browser、Render 和启用减少动画的 Experience 各有完整通过记录。在合成站点可以保留登录 Cookie 并删除当前来源的离线存储，保留相邻端口和另一个顶层站的嵌入分区。两个并行同名下载中，来源标签关闭后取消一项，另一份 4 MiB 文件正常完成并逐字节核对。向两个网页进程的故障恢复路径注入诊断故障后，重新导航和退出清理正常。

| 公网兼容性抽查 | HTTP | DOM 状态 | 本次加载记录的资源数 |
| --- | ---: | --- | ---: |
| GitHub 项目页 | 200 | complete | 122 |
| MDN 首页 | 200 | complete | 81 |
| 个人站项目详情页（站点更新前） | 200 | complete | 9 |

使用 WinUI 应用本身导航，各页面标题和来源已核对。未登录，未测试在线支付或特定账户。性能表和测试环境见[性能报告](R161-PERFORMANCE.md)。


公开 ZIP 和校验清单均从无认证下载入口重新获取。两个 ZIP 的字节数和 SHA-256 与 GitHub 资产详情、公开清单和标签 Actions 的原件一致。核对两包内 EXE 与 DLL 的 PE 架构和程序集的完整发布提交。标签原生 ARM64 自检为 282 通过、0 失败、1 项环境跳过；原生 x64 为 280 通过、0 失败、3 项环境跳过。重新下载的两包在本机各 281 通过、0 失败、2 项环境跳过。差异来自测试卷是否提供独立的 8.3 短名称，以及是否有可用的 SUBST 映射。

重新下载的两个公开包分别解压至新的隔离目录，运行完整 WinUI Smoke，每包各 40 次新建、加载和关闭标签。两次诊断垃圾回收后遗留的已关闭控件数均为零。两次进程均正常退出，报告确认保险箱已锁定、控件释放、会话目录清除。ARM64 公开包另跑 Browser 全套，复核权限、剪贴板选项、来源隔离、下载与真实进程恢复，最终通过。

最终候选另跑 3.01 分钟、12 次临时标签循环，保留页面状态读回与退出清理均通过。使用重新下载的 r16.1 ARM64 程序集恢复公开 r16 实际生成的备份，原密码可以分别解锁主空间和诱饵空间，完整解密后的 1,300,007 字节和 925,031 字节文件均与原始 SHA-256 一致。该验证使用同一 Windows 用户的 DPAPI。

官网裸地址返回 HTTP 200。首页、404 页、脚本、样式及图片共 12 个文件逐一与正式标签的 Git blob 比对一致，两个 ZIP 按钮分别返回正确架构的归档。真实 Edge 检查 1440 像素桌面以及 390、320 像素移动仿真视口，菜单、FAQ 与下载按钮均可操作。[官网](https://canonliuliang.github.io/StealthWorkspace/) · [生产部署](https://github.com/canonliuliang/StealthWorkspace/actions/runs/44788449507)

个人网站生产提交为 [e91a130](https://github.com/canonliuliang/liu-digital-space/commit/e91a130f07c8794661159c1385afbf4585401ce3)。首页、作品列表、下载页、公告、RSS、新截图均可匿名访问。四份 HTML 经换行归一后完整匹配，RSS 和图片逐字节匹配。新闻与 RSS 采用 GitHub 的实际发布时间，手机 390 像素和桌面 1440 像素页面均通过视觉检查。发布沿用已有 Windows 手动升级渠道。[个人首页](https://liu-digital-space.pages.dev/) · [软件下载](https://liu-digital-space.pages.dev/work/stealthworkspace/) · [更新公告](https://liu-digital-space.pages.dev/updates/#stealth-r16-1-preview) · [RSS](https://liu-digital-space.pages.dev/feed.xml) · [生产部署](https://github.com/canonliuliang/liu-digital-space/actions/runs/44788667797)

## r16 公开发行

GitHub 于 **2026-11-20 16:15:44 UTC（UTC+8 为 11 月 21 日 00:15:44）**发布 `v2026.11.20-r16-preview`。标签指向 `238f53b13566019a1e72b1246bd9a6d76313f4c9`。正式分发渠道为未签名预览版，便携包解压后运行。[GitHub Release](https://github.com/canonliuliang/StealthWorkspace/releases/tag/v2026.11.20-r16-preview) · [PR #6](https://github.com/canonliuliang/StealthWorkspace/pull/6) · [代码提交](https://github.com/canonliuliang/StealthWorkspace/commit/238f53b13566019a1e72b1246bd9a6d76313f4c9)

正式标签的原生 ARM64 runner 通过 233 项、跳过 1 项，x64 runner 通过 231 项、跳过 3 项，两者均为 0 失败。两个 runner 都缺少覆盖测试目录的现存 SUBST 映射；x64 文件系统另有两个场景未提供独立的 8.3 短名称。两个架构的核心报告与 ZIP 一同保存在 Actions 工件中。[正式标签构建和发布](https://github.com/canonliuliang/StealthWorkspace/actions/runs/44770942372) · [main 构建](https://github.com/canonliuliang/StealthWorkspace/actions/runs/44770830049)

| 公共资产 | 字节 | SHA-256 |
| --- | ---: | --- |
| Windows ARM64 ZIP | 95,688,173 | `15336d1796fbf5c74097825e2d9fdec09a7c69e46430e98ce316f16bc39acc00` |
| Windows x64 ZIP | 98,065,820 | `b53cd1ce30895854922297f9abc6362a88557712f4247110f336a6e35e80c302` |

官网首页、404 页、脚本、样式和五个图片资源已从公网读取并与正式标签的 Git blob 核对，两处架构按钮均指向本版 ZIP。预发布资产尚未齐全时，第一次 main 工作流保留了旧站；发布完成后第二次构建与部署均成功。[官网下载页](https://canonliuliang.github.io/StealthWorkspace/#download) · [官网生产部署](https://github.com/canonliuliang/StealthWorkspace/actions/runs/44771582100)

匿名重新下载这两个完整 ZIP，校验值与公开清单、GitHub Release 资产 digest 和正式标签 Actions 原件一致。解压后两份程序集的版本均指向上述发布提交。在本机再次运行公开包的核心自检，每架构均为 232 通过、0 失败、2 个 SUBST 环境跳过。

两个公开包均在 Windows 11 ARM64 上从隔离目录启动，ARM64 原生运行，x64 通过系统兼容层运行。每次界面测试均完成 40 次标签打开、加载和关闭，退出码为 0。退出后，报告确认保险箱已锁定、浏览器控件已释放、会话配置目录已移除。

个人站的[首页](https://liu-digital-space.pages.dev/)、[项目下载页](https://liu-digital-space.pages.dev/work/stealthworkspace/)及[版本公告](https://liu-digital-space.pages.dev/updates/#stealth-r16-preview)均在生产域名返回 r16。RSS 首条 Stealth r16 公告使用 GitHub 的公开时间并链接到上述公告。个人站源码提交为 `c350f7e1c07c2660b73a12c0719338b9e38111a0`。[RSS](https://liu-digital-space.pages.dev/feed.xml) · [个人站生产部署](https://github.com/canonliuliang/liu-digital-space/actions/runs/44771794291)

## r16 本地发布候选与升级

Windows 11 ARM64（Parallels 26300，WebView2 157.0.4341.60），自包含 .NET 10.0.15。ARM64 和 x64 解压包各通过 232 项核心检查，0 失败，2 个 SUBST 环境跳过。两架构都通过全界面巡检、锁定、控件释放与临时配置清理。x64 本机 UI 测试在 ARM64 兼容层运行。隔离诊断还验证了裸启动和核心自检没有改写合成的旧保险箱。

ARM64 额外通过完整浏览器回归、减少动态效果的体验检查和前台渲染检查。三分钟测试进行了 12 次标签创建与关闭，保留的三个页面均恢复了原表单状态，最终会话清理成功。宿主私有内存采样峰值为 82.3 MiB，渲染进程另计。这次短时测试不足以判断全天内存增长或续航。

升级测试使用公开 r15 ARM64 创建新的隔离保险箱并收藏回环测试网页。r16 解压包使用同一凭据打开同一目录，原收藏标题、地址和实际导航均核对通过，最近关闭列表为空。r15 在该次收藏面板展开的退出过程中写完清理记录后发生一次应用崩溃。r16 同场景退出码为 0，清理记录齐全。此处只记录观察结果。

图片入库的 32 MiB 限制针对最终载荷。运行时没有提供资源长度时，宿主取得完整内容后才能检查大小，处理超大图片的峰值内存可能更高。各项来源、操作步骤与性能取舍见[研究与架构记录](R16-REVIEW.md)、[性能测量](R16-PERFORMANCE.md)和[隐私验证](R16-PRIVACY-TESTS.md)。

## r15 公开发布与网站验收

2026-11-14 16:59:29 UTC（本地 11 月 15 日），[r15 预览版](https://github.com/canonliuliang/StealthWorkspace/releases/tag/v2026.11.14-r15-preview) 已公开。标签 `v2026.11.14-r15-preview` 固定于提交 `631e123fdd7b57fd71ce009f0607959418073fda`，由 [PR #5](https://github.com/canonliuliang/StealthWorkspace/pull/5) 合并。以下是发布后的验证，后续文档补充不移动标签或替换 ZIP。

[正式标签构建](https://github.com/canonliuliang/StealthWorkspace/actions/runs/43618278910)的两个架构与发布任务全部成功；[main 构建](https://github.com/canonliuliang/StealthWorkspace/actions/runs/43618271892)也成功。ARM64 为 207 通过 / 0 失败 / 1 跳过；x64 为 205 通过 / 0 失败 / 3 跳过。两者均跳过缺少现存 SUBST 映射的边界场景，x64 另有两个真实 8.3 别名场景因测试卷未提供短名称而跳过；未将跳过计为通过。两架构的 Removal SUBST 场景实际通过。

从公开下载地址匿名完整下载 ZIP 与 `SHA256SUMS.txt`，重新计算 SHA-256，同时与公开清单、GitHub 资产 digest、正式标签构建产物比对一致：

| 架构 | 下载字节数 | SHA-256 |
| --- | ---: | --- |
| ARM64 | 95,623,219 | `fac35b13721c9bb1127fcf271fadc40069f572fb89f404292fcc23aee6ac1cd8` |
| x64 | 98,000,842 | `67852274c024372af33b4d1af9f098a8ef1911764ef16c59a824df714dbd44c0` |

两包实际运行时与 ProtectedData 为 10.0.15，24 份第三方许可文件齐全，路径及私有数据排除检查通过。完整解压公开包后，两架构再次执行隔离 UI 巡检并通过；退出时保险箱锁定、浏览器控件释放、临时会话清理正常。下载后 x64 巡检仍在 ARM64 主机兼容层运行，原生 x64 的远程结果限于构建和核心检查。

[官网部署](https://github.com/canonliuliang/StealthWorkspace/actions/runs/43618601895)的 build 与 deploy 都成功。在资源齐全前的首次 main 推送中，门禁正确跳过部署并保留原站；Release 发布后才重新部署。公网 8 个页面/样式/图片文件均 HTTP 200，Git blob 与标签中的原文件一致。桌面与 390 宽度手机页面、下载锚点、404 返回入口、图片和控制台均已检查。官网部署文件原始总大小由 601,492 字节降到 258,957 字节，减少约 56.9%；这是文件体积，不代表网络时间或性能评分。

[个人网站](https://liu-digital-space.pages.dev/)也已同步 Stealth r15 提示、项目详情、Logo、真实截图与带实际发布时间的近况/RSS。桌面和手机展示已核对，保留原有粒子首页、其他作品与整体风格。

发布证据保存在 `artifacts/publication-20261115`：公开包与校验记录位于 `public-downloads`，正式标签原始核心报告位于 `tag-run-43618278910`，下载后 UI 记录位于 `public-ui-win-arm64` 和 `public-ui-win-x64`。线上网站文件比对与本地/线上界面检查分别保存，未把“工作流存在”当作部署成功。

公开更新前的源码已保存到 `backup/public-before-r15-20261115`（原 main `7260c3dd20e6f64db57ac2443360ce327bd28807`）。本机完整原版本仍可运行 `./tools/Restore-PreOverhaul.ps1` 恢复到新的旁路目录并逐文件校验；不会覆盖当前项目或用户数据。公网回退应从备份标签创建恢复分支并复核后发布，不移动已经公开的版本标签。

## r15 发布前 .NET 10 复验

2026-11-15，发布前将目标框架升级为 .NET 10 LTS，运行时及 ProtectedData 固定为 10.0.15。下方较早的 .NET 8 验收与性能记录保留，不把不同运行时的结果混作同一性能对比。

- Debug ARM64 构建零警告、零错误；Debug、ARM64 Release、x64 Release 各 206 项核心检查通过、0 失败、2 项 SUBST 环境限制跳过。x64 本地仍在 ARM64 兼容层运行。
- 两架构完整 UI 巡检，以及 ARM64 浏览器、图片阅读、渲染、持续运行和诊断隔离复验全部通过。独立的 Release 窗口经 WinApp CLI 检查并截图，正常退出时保险箱锁定、控件释放与会话清理均通过。
- 持续运行 3.01 分钟、12 轮；合成场景峰值应用私有内存 71.7MiB，保留页面状态与会话清理通过。渲染 rAF 中位间隔约 16.7ms，应用两次样本 p95 为 16.9/17.1ms，均无大于 33.5ms 的帧间隔。它们是新运行时回归记录，不是长期稳定性或普遍性能提升的证明。
- 用实际新旧程序集，在同一 Windows 用户下完成 .NET 8→10 及 10→8 合成数据兼容检查：设置、主/诱饵空间、解密、导出和备份恢复均一致，原密文与设置哈希未变；不代表换机恢复。
- 两份自包含包的实际 CoreCLR、hostfxr 与 DPAPI 组件均核对为 10.0.15，包内程序集与受测程序集一致，包含对应运行时许可证。包保存在 `artifacts/releases/2026.11.14-r15-net10/{win-arm64,win-x64}`，证据在 `artifacts/runtime-net10`。
- 官网本地桌面及 390/320 宽度检查、锚点导航、手机菜单、键盘 FAQ、静态构建与 4 项发布门禁测试通过，未发现横向溢出或控制台错误。线上部署另行记录。

## r15 本地全面重构验收

2026-11-14 至 15，在同一 Windows 11 ARM64 主机完成。业务源码与诊断源码检查点为 `264f437cd1d32c852d37018fac38283ac2f07360`，本轮没有远程发布或网站部署。

| 检查 | 结果 | 范围 |
| --- | --- | --- |
| Debug 构建 | 0 警告 / 0 错误 | ARM64，本机 SDK |
| Debug 核心自检 | 206 通过 / 0 失败 / 2 跳过 | 迁移、重置防回流、锁定竞态、文件保护、加密与恢复 |
| ARM64 / x64 发布包 | 均构建与打包成功；各 206 通过 / 0 失败 / 2 跳过 | x64 运行于 ARM64 兼容层 |
| 两架构 UI 巡检 | 均通过 | 全页面、菜单实际打开/复用、导入、锁定、最小化、40 次标签循环、退出清理 |
| ARM64 浏览器专项 | 通过 | Cookie 范围、弹窗、原生查找/缩放、图片阅读器、关闭来源标签后的共享下载、进程恢复 |
| ARM64 媒体体验 | 通过 | 长图、连续切图、坏图恢复、返回筛选/选择、加载时锁定 |
| ARM64 渲染审计 | 通过 | 本地合成网页；不宣称 FPS 提升 |
| ARM64 持续运行 | 通过 | 3.01 分钟、12 轮；DOM 保留与退出清理正常，非长期泄漏证明 |
| 诊断隔离 | 通过 | 程序副本旁合成旧数据逐文件校验不变；裸诊断自动选独立根 |
| 发布归档 | 校验值与清单一致，内部程序集与被测程序一致 | ARM64 542 项、x64 546 项，未发现私有数据文件 |
| 完整旧版恢复演练 | 18,642 个文件 SHA-256 全部一致 | 恢复到新目录，保留当前工作区 |
| 站点与发布工具 | 站点检查通过，4 项测试通过 | 未更改公网版本 |

最终归档位于 `artifacts/releases/2026.11.14-r15-accepted/{win-arm64,win-x64}`；原始结果位于 `artifacts/overhaul-final/accepted-*`。详细性能、图片、持续运行记录、已知边界和一键恢复方法见 [全面重构报告](OVERHAUL-2026-11-14.md)。

两项跳过均与 SUBST 映射环境有关，没有把跳过算作通过。保留失败日志：下载面板迟到聚焦问题已修复；首轮 x64 菜单验收过早采样，改为等待真实打开/关闭事件后，保留更严格行为断言并通过复测。所有 UI 自动验收均要求退出时保险箱已锁定、浏览器控件已释放、临时会话目录已清理。

## r12 预览版，本机发布前检查

2026-11-14，在 Windows 11 ARM64 上：

| 检查 | 结果 | 范围 |
| --- | --- | --- |
| Release ARM64 编译 | 通过，0 警告 / 0 错误 | 本机 .NET SDK 10.0.401 |
| ARM64 发布包核心自检 | 133 通过 / 0 失败 | 加密、流式读取、备份、地址解析等 |
| x64 发布包核心自检 | 133 通过 / 0 失败 | 本机 x64 兼容层，非原生 x64 实机 |
| 原生 UI 基本操作 | 通过 | 独立 fixture 启动、新建标签、结束会话、正常退出 |
| 官网页面 | 通过 | 桌面与手机布局、锚点、FAQ、图片引用；托管部署需另外验证 |
| 发布包内容 | 通过 | 新目录打包，含第三方许可，排除保险箱与浏览器会话 |

新增日志隐私回归使用包含私密路径和 URL 的合成异常，检查警告与错误日志不保留原始消息和源码文件路径，同时保留异常类型与错误码。它不等于对所有日志内容做了形式化隐私证明。

CI 在 Windows x64 与 Windows ARM64 runner 上独立构建并运行核心自检。请以对应提交的 Actions 结果为准；工作流文件存在本身不表示远程检查成功。

## r12 公开发布验收

2026-11-14，发布标签 `v2026.11.14-r12-preview` 对应源码提交 `3d4b2f72108c2f9a9de808010ee5ad2f7e988b19`。后续验证文档更新不改变该标签或已发布的 ZIP。

| 检查 | 实际结果 | 记录 |
| --- | --- | --- |
| 原生 Windows x64 CI | 构建、打包成功；133 通过 / 0 失败 | [发布工作流](https://github.com/canonliuliang/StealthWorkspace/actions/runs/43540068700) |
| 原生 Windows ARM64 CI | 构建、打包成功；133 通过 / 0 失败 | 同一发布工作流的 ARM64 job |
| GitHub Release | 两个架构 ZIP 和 SHA256SUMS.txt 均已上传 | [r12 预览版](https://github.com/canonliuliang/StealthWorkspace/releases/tag/v2026.11.14-r12-preview) |
| 真实公开下载 | 下载两个完整 ZIP，SHA-256 均与发布清单一致 | x64 95,822,269 字节；ARM64 93,860,529 字节 |
| 下载包检查 | 必需程序、图标、快捷方式脚本、文档和版本元数据齐全；未发现保险箱、会话或密钥文件 | x64 546 个条目；ARM64 542 个条目 |
| GitHub Pages | 构建、部署成功；公网首页 HTTP 200 | [官网部署](https://github.com/canonliuliang/StealthWorkspace/actions/runs/43540452332) |
| 公网界面 | 1280px 桌面、390px 手机布局无横向溢出；FAQ 展开互斥正常；下载入口分别指向正确架构 | [官网](https://canonliuliang.github.io/StealthWorkspace/) |

发布包 SHA-256：

```text
e75962b372bfaf704b21ffdd08ee0cef0bf83c85ee20d8f5f7256f599531c51e  Stealth-2026.11.14-r12-preview-win-x64.zip
a251d197267e760f449dda33dd880637544240865f3858edf216c48f41e88fef  Stealth-2026.11.14-r12-preview-win-arm64.zip
```

本次对公网 ZIP 执行了完整下载、校验值和包内容核验，没有把它当作已完成下载后双击启动测试的证据。应用 UI 启动与交互验证使用的是本机源码构建及独立合成数据。

## 已知覆盖空缺

- 未签名预览版的 SmartScreen 体验尚未解决；源码启动通过不代表任意下载后的双击启动都通过。
- 尚未完成 Windows 10、所有显卡/虚拟机和第三方站点的覆盖验证。
- 网站安全验证、系统解码器、休眠是否被 WebView2 接受均依赖外部条件。
- 当前备份依赖原 Windows 用户，跨电脑恢复不在当前功能范围内。
- 没有第三方安全审计，不宣称匿名上网、物理擦除或完全阻断截屏。

历史 r11 的大量浏览器交互与合成媒体回归为本机诊断；原始网络日志、个人路径和测试产物不随公共仓库上传。性能结果只适用于对应测试场景，不推导所有网页的统一提速比例。
