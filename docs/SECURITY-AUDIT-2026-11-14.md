# 2026-11-14 审计复核与修复

针对用户提供的 12 条审计逐项核实。以下内容描述实际代码修改，不代表整个应用经过正式安全认证。

| 问题 | 复核与处理 |
| --- | --- |
| 配置损坏时清理加密媒体 | 成立。配置损坏、备份回退、未初始化或恢复日志存在时停止自动清理；自动清理仅移除过期临时文件，不再推断未引用的 `.svault` 可以删除。 |
| 第二次改密删除源文件 | 成立。每次轮换生成独立随机文件名，目标使用 `CreateNew`；禁止覆盖现有文件。同步备份索引后才清理旧代文件；清理失败保留恢复日志。 |
| 深层 Junction 越界清理 | 成立。显式逐层遍历，检查根目录和父路径，跳过每层重解析点；删除前再校验。 |
| 主密码与诱饵密码碰撞 | 成立。按另一空间自己的盐和迭代数派生并比对，创建、修改均拒绝相同密码。 |
| Explorer 跨会话判断 | 成立。检查和结束资源管理器进程均限定当前 Windows 会话。 |
| 直接关闭窗口清理顺序 | 成立。统一异步退出任务，立即锁定保险箱，再关闭标签，最后删除会话目录；显式控制应用退出，使 UI 调度器继续完成释放。 |
| `UseShellExecute=true` 不能使用 `ArgumentList` | 该断言不成立。.NET 8 Windows 的 ShellExecute 路径调用 `BuildArguments()`。本轮没有把这项计为已修复故障，也没有据此修改 Edge 启动参数。 |
| 设置页忽略冷却状态 | 成立。显示剩余等待秒数，保留配置损坏提示；提示滚入可见区域。 |
| 粉碎失败假报成功、硬链接覆写 | 成立。移除普通删除降级；同一排他句柄上校验文件、覆写、标记删除。多重硬链接保留并报告未删除，避免破坏其他文件名对应的数据。 |
| 剪贴板历史返回值被忽略 | 成立。使用 `ClearHistory()` 实际返回值，区分未开启和失败。 |
| UI 集合在锁内或后台修改 | 成立。窗口绑定 UI 上下文；锁内只取快照，锁外在 UI 上下文发布集合及状态事件。后台锁定立即清除密钥。 |
| 迁移失败遗留暂存目录 | 成立。`finally` 仅清理本次未发布的暂存目录，不删除源目录或目标保险箱。 |

同时对保险箱变更操作增加串行门，避免导入、轮换、恢复和清理相互覆盖；异常恢复日志不会被静默丢弃，无法确认时保留数据并阻止进一步写入。

## 验证方式与限制

本地修复版本：`2026.11.14-r13-preview`。本轮未更新官网、未创建远程发布，也未覆盖正式安装目录。

| 实际检查 | 结果 |
| --- | --- |
| `dotnet build`，Windows 11 ARM64 | 0 警告、0 错误 |
| 核心自检 | 154 通过、0 失败，其中新增 21 项审计回归 |
| 已加载网页时直接 `Window.Close()` | 通过；先锁定、释放控件，约 1.2 秒内完成会话目录清理 |
| WebView 初始化期间直接关闭 | 通过；初始化与释放完成后退出，目录无残留 |
| 完整 UI 页面走查 | 最终 `SMOKE RESULT: OK`；含媒体导入、浏览器交互、锁定/解锁和带活动网页退出 |
| WinApp CLI 设置页操作 | 连续错误密码显示等待秒数；修复后的消息滚入可见区域，截图已核对 |

本地证据在忽略版本控制的 `artifacts/`：`audit-r13-selftest-3.txt`、`audit-r13-shutdown-2.txt`、`audit-r13-pending.txt`、`audit-r13-full-final.txt`、`audit-r13-visible-message.png`。第一次完整 UI 走查在崩溃恢复等待处失败，最终构建的完整走查已通过；不把本机通过推导为所有设备稳定性已验证。

- 所有回归保险箱和媒体均为独立生成的测试数据，测试构造器不迁移正式数据。
- 核心回归包含连续三轮主/诱饵改密及完整明文比对、备份回退、轮换中断恢复、文件占用失败、配置损坏、主/诱饵密码碰撞、后台集合通知、硬链接、深层 Junction 和迁移失败。
- WinApp CLI 验证设置页冷却提示；退出测试使用本地合成网页和独立会话目录，不访问用户网页。
- 未通过实际快速用户切换杀进程验证 Explorer 修复，也不为测试清除真实剪贴板。
- 路径检查降低越界风险，但没有宣称抵御同一权限攻击者对父目录反复交换的所有竞争条件。
- 软件覆写无法保证 SSD、写时复制文件系统或已有备份中的物理数据消失。
- 加密文件已被旧版删除时，本修复无法凭空恢复；来源文件和独立备份仍然重要。

## 核对资料

- [.NET 8 Windows Process 启动实现](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Win32.cs)
- [按文件句柄修改文件状态](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle)
- [显式控制 WinUI 调度器退出](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.application.dispatchershutdownmode)
- [WebView2 浏览器退出与资源释放](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2environment5)
