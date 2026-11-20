using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using StealthWorkspace.Services;
using Windows.System;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    private readonly List<string> _blockedPopupUrls = new();
    private int _blockedPopupCount;
    private bool _popupPending;
    private long _backgroundClickAt;

    private static bool IsKeyPressed(VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource
        .GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void BrowserPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var properties = args.GetCurrentPoint(_browserControl).Properties;
        _backgroundClickAt = !IsKeyPressed(VirtualKey.Shift) && (properties.IsMiddleButtonPressed || IsKeyPressed(VirtualKey.Control))
            ? Environment.TickCount64 : 0;
    }

    private string? PopupOrigin => Uri.TryCreate(CurrentUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        ? uri.GetLeftPart(UriPartial.Authority) : null;

    private bool IsSupportedPopup(string address) => string.IsNullOrEmpty(address) || address == "about:blank" ||
        TryCreateWebUri(address, out _) || (PopupOrigin is { } origin && address.StartsWith("blob:" + origin + "/", StringComparison.OrdinalIgnoreCase));

    private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (_isDisposed || PopupRequested == null || !IsSupportedPopup(args.Uri)) return;
        string? frameAddress = null;
        bool userInitiated = args.IsUserInitiated;
        if (!userInitiated && _tabPermissions.AutomaticPopupsAllowed)
        {
            // Source is the requesting frame's URL at the moment of the request.
            // Older runtimes may lack this property. Keep their normal click path.
            try { frameAddress = args.OriginalSourceFrameInfo?.Source; }
            catch (Exception ex) { AppLog.Warn("Popup frame source unavailable: " + ex.GetType().Name); }
        }
        ulong documentId = _imageDocumentId;
        bool CanOpen() => !_isDisposed && !_retainedForDownloads && documentId == _imageDocumentId &&
            (userInitiated || _tabPermissions.AllowsAutomaticPopupFrom(frameAddress));
        if (_popupPending || !CanOpen())
        {
            RecordBlockedPopup(args.Uri);
            return;
        }

        // A real child WebView preserves POST submissions, window.opener, named
        // windows and window.close. Navigating a copied URL loses that context.
        var deferral = args.GetDeferral();
        _popupPending = true;
        bool background = userInitiated && !IsKeyPressed(VirtualKey.Shift) &&
            (IsKeyPressed(VirtualKey.Control) || (_backgroundClickAt != 0 && Environment.TickCount64 - _backgroundClickAt < 750));
        _backgroundClickAt = 0;
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            BrowserTabControl? child = null;
            try
            {
                if (!CanOpen() || PopupRequested == null) return;
                child = await PopupRequested(background);
                if (!CanOpen() || child?._hookedCore == null || child._isDisposed)
                {
                    child?.CloseRequested?.Invoke();
                    return;
                }
                args.NewWindow = child._hookedCore;
            }
            catch (Exception ex)
            {
                child?.CloseRequested?.Invoke();
                if (!_isDisposed) ShowBrowserError("无法打开新标签页", "请重新点击链接。");
                AppLog.Warn("New window failed: " + ex.GetType().Name);
            }
            finally
            {
                _popupPending = false;
                try { deferral.Complete(); } catch (System.Runtime.InteropServices.COMException) { }
            }
        }))
        {
            _popupPending = false;
            deferral.Complete();
        }
    }

    internal async Task<bool> PreparePopupAsync()
    {
        _focusAddressOnLoad = false;
        await InitializeAsync(null);
        return !_isDisposed && _hookedCore != null;
    }

    private void CoreWebView2_WindowCloseRequested(CoreWebView2 sender, object args)
    {
        if (!_isDisposed) CloseRequested?.Invoke();
    }

    private void RecordBlockedPopup(string address)
    {
        _blockedPopupCount = Math.Min(999, _blockedPopupCount + 1);
        if (TryCreateWebUri(address, out var uri) && uri != null && !_blockedPopupUrls.Contains(uri.AbsoluteUri))
        {
            _blockedPopupUrls.Add(uri.AbsoluteUri);
            if (_blockedPopupUrls.Count > 5) _blockedPopupUrls.RemoveAt(0);
        }
        UpdatePopupButton();
    }

    private void UpdatePopupButton()
    {
        bool allowed = _tabPermissions.AutomaticPopupsAllowed;
        bool visible = allowed || _blockedPopupCount > 0;
        UiMotion.SetVisible(PopupBlockedBtn, visible, MotionCue.Fade);
        if (!visible) return;
        string label = allowed ? "管理此站点的自动弹窗例外" : $"已拦截 {_blockedPopupCount} 个自动弹窗";
        ToolTipService.SetToolTip(PopupBlockedBtn, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PopupBlockedBtn, label);
    }

    internal void SetAutomaticPopupsForSite(string expectedOrigin, bool allowed)
    {
        _tabPermissions.SetAutomaticPopups(expectedOrigin, allowed);
        UpdatePopupButton();
    }

    private void PopupBlockedBtn_Click(object sender, RoutedEventArgs args)
    {
        ulong documentId = _imageDocumentId;
        var panel = new StackPanel { Spacing = 10, Width = 280 };
        panel.Children.Add(new TextBlock { Text = _blockedPopupCount == 0 ? "此站点的自动弹窗" : $"已拦截 {_blockedPopupCount} 个自动弹窗", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var flyout = new Flyout { Content = panel };
        foreach (string address in _blockedPopupUrls.AsEnumerable().Reverse())
        {
            var open = new Button { Content = new Uri(address).Host, HorizontalAlignment = HorizontalAlignment.Stretch };
            ToolTipService.SetToolTip(open, address);
            open.Click += (_, _) => { flyout.Hide(); if (documentId == _imageDocumentId) NewTabRequested?.Invoke(address, false); };
            panel.Children.Add(open);
        }
        if (_tabPermissions.Origin is { } origin)
        {
            var allow = new CheckBox { Content = "离开此站点前允许自动弹窗", IsChecked = _tabPermissions.AutomaticPopupsAllowed };
            void Changed(object sender, RoutedEventArgs e)
            {
                if (documentId != _imageDocumentId) return;
                SetAutomaticPopupsForSite(origin, allow.IsChecked == true);
            }
            allow.Checked += Changed;
            allow.Unchecked += Changed;
            panel.Children.Add(allow);
        }
        flyout.ShowAt(PopupBlockedBtn);
    }
}
