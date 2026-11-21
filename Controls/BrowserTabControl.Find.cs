using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using StealthWorkspace.Services;
using Windows.System;

namespace StealthWorkspace.Controls;

public sealed partial class BrowserTabControl
{
    internal void FindFromShortcut(bool advance, bool backwards)
    {
        if (_nativeFindDispatching) return;
        if (FindBar.Visibility == Visibility.Visible)
        {
            if (advance) _ = RunFindAsync(backwards, advance: true);
            else { FindTextBox.Focus(FocusState.Keyboard); FindTextBox.SelectAll(); }
            return;
        }
        if (_isInitialized && _hookedCore != null && !_nativeFindUnavailable)
        {
            _ = SendNativeFindAsync(advance, backwards);
            return;
        }
        if (FindBar.Visibility != Visibility.Visible || !advance) ToggleFindBar();
        if (advance) _ = RunFindAsync(backwards, advance: true);
    }

    private CoreWebView2Find? _nativeFind;
    private bool _nativeFindUnavailable;
    private string _findTerm = "";
    private bool _findCase;
    private int _findVersion;
    private readonly SemaphoreSlim _findGate = new(1, 1);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _findTimer;
    private bool _findAfterMenuClose;
    internal bool IsNativeFindDispatching => _nativeFindDispatching;
    private bool _nativeFindDispatching;

    private async Task SendNativeFindAsync(bool advance, bool backwards)
    {
        if (_nativeFindDispatching || _isDisposed || !IsForegroundTab || _browserControl is not { } browser) return;
        _nativeFindDispatching = true;
        try
        {
            FindBar.Visibility = Visibility.Collapsed;
            browser.Focus(FocusState.Programmatic);
            // XAML focus is not an acknowledgement from Chromium's input host.
            // Let pending input/flyout focus restoration finish before forwarding.
            var idle = new TaskCompletionSource<bool>();
            if (DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => idle.TrySetResult(true)))
                await idle.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
            if (_isDisposed || !IsForegroundTab) return;
            browser.Focus(FocusState.Programmatic);
            if (_isDisposed || !IsForegroundTab || browser.FocusState == FocusState.Unfocused) return;
            if (_hookedCore is { } core)
            {
                string focused = await core.ExecuteScriptAsync("document.hasFocus()").AsTask().WaitAsync(TimeSpan.FromMilliseconds(500));
                if (SmokeTest.Enabled) SmokeTest.Report("find document focused=" + focused);
            }
            if (_isDisposed || !IsForegroundTab || !BrowserZoomInput.IsOwnerForeground(MainWindow.CurrentHandle)) return;
            if (SmokeTest.Enabled) SmokeTest.Report($"find input: advance={advance}; backwards={backwards}; ownerForeground={BrowserZoomInput.IsOwnerForeground(MainWindow.CurrentHandle)}; focus={browser.FocusState}");
            // Let Chromium retain the current query and its own F3/Shift+F3
            // semantics. StartAsync(empty options) erases the user's find text;
            // FindNext alone silently does nothing before a session exists.
            if (BrowserZoomInput.SendFind(MainWindow.CurrentHandle, advance, backwards))
                await Task.Delay(100); // Keep re-entry guarded while input is dispatched.
            else ShowFallbackFindBar();
        }
        catch (Exception ex)
        {
            if (_isDisposed) return;
            AppLog.Warn("Native find shortcut: " + ex.GetType().Name);
            if (IsForegroundTab && BrowserZoomInput.IsOwnerForeground(MainWindow.CurrentHandle)) ShowFallbackFindBar();
        }
        finally { _nativeFindDispatching = false; }
    }

    private void FindInPageBtn_Click(object sender, RoutedEventArgs e)
    {
        _findAfterMenuClose = true;
        MoreActionsFlyout.Hide();
    }

    private void MoreActionsFlyout_Closed(object sender, object args)
    {
        if (_edgeDocumentAfterMenuClose is { } document)
        {
            _edgeDocumentAfterMenuClose = null;
            // The dialog and the menu have separate native popups. Let the menu
            // finish releasing its input surface before presenting the handoff.
            DispatcherQueue.TryEnqueue(() => { _ = OpenInEdgeAsync(document); });
        }
        if (_historyAfterMenuClose && !_isDisposed)
        {
            _historyAfterMenuClose = false;
            OpenSessionHistory();
        }
        if (_zoomAfterMenuClose is { } key && !_isDisposed)
        {
            _zoomAfterMenuClose = null;
            _ = SendNativeZoomAsync(key);
        }
        if (_imagesAfterMenuClose && !_isDisposed)
        {
            _imagesAfterMenuClose = false;
            _ = OpenPageImagesAsync();
        }
        if (!_findAfterMenuClose || _isDisposed) return;
        _findAfterMenuClose = false;
        // Use the visible host find bar for an explicit menu command. The
        // programmatic Find API searches the document without synthesizing Ctrl+F.
        if (_documentReady && _hookedCore != null && IsForegroundTab) ShowFallbackFindBar();
    }

    private void ToggleFindBar()
    {
        if (!_nativeFindUnavailable && _hookedCore != null)
        {
            _ = SendNativeFindAsync(advance: false, backwards: false);
            return;
        }
        ShowFallbackFindBar();
    }

    private void ShowFallbackFindBar()
    {
        UiMotion.SetVisible(FindBar, true, MotionCue.FromAbove);
        FindTextBox.Focus(FocusState.Programmatic);
        FindTextBox.SelectAll();
        QueueFind();
    }

    private void CloseFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        StopFind();
        _browserControl?.Focus(FocusState.Programmatic);
    }

    private void CloseFindBar_Click(object sender, RoutedEventArgs e) => CloseFindBar();
    private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e) => QueueFind();
    private void FindCaseToggle_Changed(object sender, RoutedEventArgs e) => QueueFind();
    private void FindNextBtn_Click(object sender, RoutedEventArgs e) => _ = RunFindAsync(false, advance: true);
    private void FindPrevBtn_Click(object sender, RoutedEventArgs e) => _ = RunFindAsync(true, advance: true);

    private void FindTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        _ = RunFindAsync(shift, advance: true);
    }

    private void QueueFind()
    {
        if (_isDisposed || FindBar == null || FindBar.Visibility != Visibility.Visible) return;
        _findVersion++; // Invalidate an older result immediately, before the debounce.
        if (_findTimer == null)
        {
            _findTimer = DispatcherQueue.CreateTimer();
            _findTimer.IsRepeating = false;
            _findTimer.Interval = TimeSpan.FromMilliseconds(180);
            _findTimer.Tick += FindTimer_Tick;
        }
        _findTimer.Stop();
        _findTimer.Start();
    }

    private void FindTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => _ = RunFindAsync();

    private async Task RunFindAsync(bool backwards = false, bool advance = false)
    {
        _findTimer?.Stop();
        if (_isDisposed || FindBar.Visibility != Visibility.Visible || _hookedCore is not { } core) return;
        int version = ++_findVersion;
        string term = FindTextBox.Text ?? "";
        bool matchCase = FindCaseToggle.IsChecked == true;
        await _findGate.WaitAsync();
        try
        {
            if (_isDisposed || version != _findVersion) return;
            if (term.Length == 0) { StopFind(); return; }
            if (_nativeFind == null && !_nativeFindUnavailable)
            {
                try
                {
                    _nativeFind = core.Find;
                    _nativeFind.MatchCountChanged += FindResultChanged;
                    _nativeFind.ActiveMatchIndexChanged += FindResultChanged;
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or NotImplementedException)
                {
                    _nativeFindUnavailable = true;
                }
            }
            if (_nativeFind is { } find)
            {
                if (_findTerm == term && _findCase == matchCase)
                {
                    if (advance) { if (backwards) find.FindPrevious(); else find.FindNext(); }
                }
                else
                {
                    find.Stop();
                    var options = core.Environment.CreateFindOptions();
                    options.FindTerm = term;
                    options.IsCaseSensitive = matchCase;
                    options.ShouldHighlightAllMatches = true;
                    options.SuppressDefaultFindDialog = true;
                    _findTerm = term;
                    _findCase = matchCase;
                    // Some runtime versions don't finish StartAsync until the next
                    // search/Stop. Never serialize the user's next keystroke behind it.
                    _ = ObserveFindStartAsync(find.StartAsync(options).AsTask(), version, backwards);
                }
                UpdateFindResult();
            }
            else
            {
                string result = await core.ExecuteScriptAsync($"window.find({System.Text.Json.JsonSerializer.Serialize(term)}, {(matchCase ? "true" : "false")}, {(backwards ? "true" : "false")}, true, false, false, false);");
                if (!_isDisposed && version == _findVersion) FindStatusText.Text = result == "true" ? "已定位" : "无匹配";
            }
        }
        catch (Exception ex)
        {
            if (!_isDisposed && version == _findVersion) FindStatusText.Text = "暂时不可用";
            if (!_isDisposed) AppLog.Warn("Find failed: " + ex.GetType().Name);
        }
        finally { _findGate.Release(); }
    }

    private void FindResultChanged(CoreWebView2Find sender, object args) => UpdateFindResult();

    private async Task ObserveFindStartAsync(Task operation, int version, bool backwards)
    {
        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            if (_isDisposed || version != _findVersion || FindBar.Visibility != Visibility.Visible) return;
            if (backwards) _nativeFind?.FindPrevious();
            UpdateFindResult();
        }
        catch (TimeoutException)
        {
            // The native result properties/events remain usable even if this
            // runtime delays its completion callback. Do not retain the waiter.
            if (!_isDisposed && version == _findVersion) UpdateFindResult();
        }
        catch (Exception ex)
        {
            if (!_isDisposed && version == _findVersion)
            {
                FindStatusText.Text = "暂时不可用";
                AppLog.Warn("Find failed: " + ex.GetType().Name);
            }
        }
    }
    private void UpdateFindResult()
    {
        if (_isDisposed || FindBar.Visibility != Visibility.Visible || _nativeFind is not { } find) return;
        FindStatusText.Text = find.MatchCount == 0 ? "无匹配" : $"{Math.Max(0, find.ActiveMatchIndex)} / {find.MatchCount}";
    }

    private void StopFind()
    {
        _findVersion++;
        _findTimer?.Stop();
        _findTerm = "";
        try { _nativeFind?.Stop(); } catch { }
        FindStatusText.Text = "";
    }

    private void DetachFind()
    {
        StopFind();
        if (_findTimer != null)
        {
            _findTimer.Tick -= FindTimer_Tick;
            _findTimer = null;
        }
        if (_nativeFind is { } find)
        {
            try { find.MatchCountChanged -= FindResultChanged; find.ActiveMatchIndexChanged -= FindResultChanged; } catch { }
        }
        _nativeFind = null;
        _nativeFindUnavailable = false;
    }

    internal async Task<bool> CheckSmokeFindAsync()
    {
        if (_hookedCore is not { } core) return false;
        SmokeTest.Report("browser: starting native find regression");
        await core.ExecuteScriptAsync("document.body.innerHTML='<p>needle NEEDLE needle</p>';");
        FindBar.Visibility = Visibility.Visible;
        FindTextBox.Text = "needle";
        await RunFindAsync();
        for (int i = 0; i < 30 && _nativeFind?.MatchCount != 3; i++) await Task.Delay(50);
        bool counted = _nativeFind?.MatchCount == 3;
        int index = _nativeFind?.ActiveMatchIndex ?? -1;
        await RunFindAsync(advance: true);
        for (int i = 0; i < 30 && _nativeFind?.ActiveMatchIndex == index; i++) await Task.Delay(50);
        bool advanced = _nativeFind?.ActiveMatchIndex != index;
        FindCaseToggle.IsChecked = true;
        await RunFindAsync();
        for (int i = 0; i < 30 && _nativeFind?.MatchCount != 2; i++) await Task.Delay(50);
        bool cased = _nativeFind?.MatchCount == 2;
        FindTextBox.Text = "missing phrase";
        await RunFindAsync();
        for (int i = 0; i < 30 && _nativeFind?.MatchCount != 0; i++) await Task.Delay(50);
        bool missing = _nativeFind?.MatchCount == 0;
        CloseFindBar();
        FindCaseToggle.IsChecked = false;
        SmokeTest.Report($"browser: native find count={counted}; next={advanced}; case={cased}; zero={missing}; closed={FindBar.Visibility == Visibility.Collapsed}");
        return counted && advanced && cased && missing;
    }
}
