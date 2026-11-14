using Microsoft.Web.WebView2.Core;

namespace StealthWorkspace.Services;

internal static class BrowserNavigationFailure
{
    internal static (string Title, string Message) Describe(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.HostNameNotResolved => ("找不到网站", "无法解析此网址。请检查域名是否输入正确，或稍后重试。"),
        CoreWebView2WebErrorStatus.Disconnected => ("网络连接已断开", "恢复网络连接后，点击重试即可重新打开此页。"),
        CoreWebView2WebErrorStatus.Timeout => ("网站响应超时", "网站或网络响应较慢。可以稍后重试，或先打开其他网页。"),
        CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.ServerUnreachable =>
            ("无法连接网站", "尚未建立到此网站的连接。请检查网址，或稍后重试。"),
        CoreWebView2WebErrorStatus.ConnectionReset or CoreWebView2WebErrorStatus.ConnectionAborted =>
            ("连接已中断", "加载过程中连接被中断。点击重试重新打开此页。"),
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or CoreWebView2WebErrorStatus.CertificateExpired or
        CoreWebView2WebErrorStatus.ClientCertificateContainsErrors or CoreWebView2WebErrorStatus.CertificateRevoked or
        CoreWebView2WebErrorStatus.CertificateIsInvalid =>
            ("无法验证网站证书", "此网站的安全证书存在问题。请检查网址与设备日期，或联系网站方。"),
        CoreWebView2WebErrorStatus.RedirectFailed => ("网页跳转失败", "网站未能完成跳转。可以重新输入网站首页地址。"),
        CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired => ("代理需要身份验证", "当前网络代理要求身份验证。请先检查正在使用的代理连接。"),
        CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse => ("网站响应异常", "网站返回了无法识别的响应。可以稍后重试。"),
        _ => ("暂时无法加载页面", "浏览内核未提供更具体的原因。请检查网址后重试。")
    };
}
