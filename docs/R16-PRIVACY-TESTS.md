# r16 浏览器隐私验证

以下检查于 2026-11-20 在 Windows ARM64 上执行。配置目录用例只使用新建的合成文件，诊断日志写入隔离数据目录。

## 被占用的会话文件

WebView2 的用户数据目录可能包含 Cookie、缓存和权限数据。控制器关闭后，运行时仍可能短暂持有文件。开发者在实际使用中报告过目录占用；该反馈无法衡量 Stealth 用户中的发生频率。[Microsoft 用户数据目录说明](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder) · [开发者讨论](https://github.com/MicrosoftEdge/WebView2Feedback/discussions/4479)

在带所有权标记、已超过 24 小时的测试目录中，以 Windows 排他文件句柄占用一个合成文件。分别通过修复前后的 Debug 程序集执行启动清理，释放文件后再次模拟符合清理条件的启动。

| 场景 | 修复前 | 修复后 |
| --- | --- | --- |
| 文件仍占用 | 返回未清除，所有权标记消失 | 返回未清除，标记保留 |
| 文件释放，执行下一次清理 | 返回未清除，目录仍在 | 返回已清除，目录已移除 |

另在修复后的独立进程中将同类测试目录放入待删除列表。首次执行实际 `ShutdownAsync` 时保持文件占用，结果为 `false` 且标记保留。释放后再次执行，结果为 `true` 且目录消失。结束会话界面依据这个返回值决定是否显示清理未完成提示。

`BrowserProfileSafetyTests` 的 9 项断言全部通过，覆盖上述启动重试、活动所有者、近期目录、无标记目录、错误名称、根目录边界和目录联接。目录联接指向的合成文件在清理后逐字核对成功。已有旧版本遗失所有权标记的目录仍需手动确认。此测试验证文件系统中可观察到的删除结果，SSD、系统快照和备份的恢复能力不在测量范围内。

## 站点权限与弹窗

默认权限按照设备能力和请求来源处理，临时例外限定在当前标签页的 HTTP(S) 协议、主机和端口。WebView2 官方说明，权限事件的 URI 对应实际申请内容；应用将 `SavesInProfile` 设为 `false`，让后续请求再次经过宿主规则。用户曾报告清除站点权限后重新请求困难。[WebView2 权限事件](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2permissionrequestedeventargs) · [用户反馈](https://github.com/MicrosoftEdge/WebView2Feedback/issues/2889)

弹窗例外按 WebView2 提供的发起帧地址检查。在子标签初始化的等待期间再次核对文档和例外，以处理导航、关闭或撤销。回归场景使用本机回环地址的两个主机名和不同端口，分别检查同源放行、跨源嵌入页面拦截和离开后返回；同一套界面走查还检查主动点击、POST、父窗口通信和后台打开。[WebView2 发起帧接口](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2newwindowrequestedeventargs.originalsourceframeinfo) · [依赖命名弹窗的实际登录场景](https://github.com/MicrosoftEdge/WebView2Feedback/issues/5664)

在本机 WinUI / WebView2 界面走查中，默认拦截、同源放行、跨主机嵌入页拦截、跨端口导航后返回仍拦截四项均通过。主动点击保留了父窗口、会话存储和 POST 方法，空白子窗可以由父页写入，后台打开保留原标签的选中状态。以上结果使用合成网页，没有使用真实登录、银行或支付账户。

本次文件清理实验没有测量第三方 Cookie 分区、DNS 解析路径、WebRTC 地址暴露、设备指纹或 SmartScreen 的实网流量。相关行为受 WebView2、Windows 和本机网络策略影响。产品的默认保护和本地数据位置见[隐私说明](PRIVACY.md)。
