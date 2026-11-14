**Stealth r15：视觉、响应与数据保护的重大更新。**

这一版重新设计了品牌与主要页面，集中降低日常交互的开销，并补齐锁定、迁移、重置和文件操作中的边界保护。多标签浏览、跨标签下载、网页与本地图片阅读、加密媒体库继续作为产品核心。

- **全新视觉。** 几何 Logo、石墨与青玉配色、统一的深浅主题；重新梳理锁屏、新标签、媒体库、播放器、设置和清理页面，补齐空状态、窄窗布局和可访问名称。
- **减少等待与重复工作。** 收藏和更多菜单按需创建并复用，减少标签切换动画、收藏重绑定和 favicon 重复抓取；地址建议先筛选再解析，大媒体库搜索合并连续输入并批量更新列表。
- **锁定优先于迟到的验证。** 密码计算移出共享状态锁，重复锁定、最小化锁定和初始化期间锁定均能撤销在途解锁结果。
- **菜单与下载衔接更可靠。** 修复新标签迟到聚焦干扰已打开菜单的问题；验证关闭来源标签后，仍可在共享列表暂停、继续下载并完成文件。
- **文件操作与旧数据保护。** 加强导出目标、导入原件身份和密文长度校验；旧设置迁移失败保留源文件，旧保险箱迁移保留原始副本，重置标记阻止旧目录自动重新导入。存储格式和密码派生强度保持兼容。
- **可复现的验收工具。** 增加隔离 UI、浏览器、性能对比和诊断隔离检查；诊断启动使用独立数据目录，避免导入程序旁的旧数据。
- **更新运行基础。** 升级到 .NET 10 LTS，发布包包含 10.0.15 运行时和对应 DPAPI 组件，保留数据格式并重新进行双架构验证。

重构阶段本机同配置 .NET 8 / Debug ARM64 测试中，空标签控件平均构造耗时从 **6.18ms 降至 4.32ms**，托管分配从 **105.1KiB 降至 81.1KiB**。10,000 条收藏的合成无匹配建议查询中位耗时从 **1.2495ms 降至 0.1727ms**。这些分别是控件构造与本地算法的测量，不代表所有网站统一提速；本轮没有证据宣称网页 FPS 提升或总进程内存下降。

本机两架构发布包各有 **206 项核心自检通过、0 失败、2 项 SUBST 环境限制跳过**，完整界面巡检通过；ARM64 另完成浏览器、图片体验、渲染、诊断隔离与三分钟持续测试。x64 在 ARM64 兼容层运行，不等同于原生 x64 实机验证。远程构建状态请查看对应版本的 Actions 记录。

**公开发布验收已完成。** [正式版本构建](https://github.com/canonliuliang/StealthWorkspace/actions/runs/43618278910)中 ARM64 207 项通过 / 1 项环境跳过，x64 205 项通过 / 3 项环境跳过，均无失败。公开 ZIP 已完整重新下载，SHA-256 与清单及构建产物一致；两份下载包的隔离界面巡检也通过。跳过条件、验证范围和校验值见[公开验收记录](https://github.com/canonliuliang/StealthWorkspace/blob/main/docs/VALIDATION.md#r15-公开发布与网站验收)。

提供 Windows x64 / ARM64 两种便携包：

- Intel / AMD：下载 `Stealth-2026.11.14-r15-preview-win-x64.zip`。
- Windows on Arm：下载 `Stealth-2026.11.14-r15-preview-win-arm64.zip`。
- 完整解压后打开 `StealthWorkspace.exe`；包内 `START-HERE.md` 包含快捷方式、更新和校验说明。
- `SHA256SUMS.txt` 包含两个 ZIP 的完整性校验值。更新前请确认重要数据另有可恢复副本。

**当前未签名**，可能遇到 SmartScreen。请自行判断信任，不要关闭系统防护。SHA-256 校验值用于核对文件完整性，不能替代发布者签名。

临时会话不等于匿名上网，普通下载不自动加密。当前保险箱备份依赖原 Windows 用户，重装或换机后可能无法恢复；不要把它当作跨电脑备份。旧保险箱迁移保留的副本和外部备份不会随当前保险箱重置而一并删除。

第三方网站验证、系统编解码器、后台休眠和部分捕获方式仍受运行环境影响。三分钟测试不等于长期稳定性或独立安全审计。

[官网下载](https://canonliuliang.github.io/StealthWorkspace/#download) · [安装指南](https://github.com/canonliuliang/StealthWorkspace/blob/main/docs/INSTALL.md) · [r15 源码](https://github.com/canonliuliang/StealthWorkspace/tree/v2026.11.14-r15-preview) · [构建与自检](https://github.com/canonliuliang/StealthWorkspace/actions/workflows/build.yml) · [完整变化](https://github.com/canonliuliang/StealthWorkspace/blob/main/CHANGELOG.md) · [实测与验收范围](https://github.com/canonliuliang/StealthWorkspace/blob/main/docs/OVERHAUL-2026-11-14.md) · [隐私与数据边界](https://github.com/canonliuliang/StealthWorkspace/blob/main/docs/PRIVACY.md)
