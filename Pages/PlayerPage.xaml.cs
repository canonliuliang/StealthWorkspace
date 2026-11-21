using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using StealthWorkspace.Models;
using StealthWorkspace.Services;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.System;

namespace StealthWorkspace.Pages;

public sealed partial class PlayerPage : Page
{
    private MediaPlayer? _mediaPlayer;
    private Stream? _plaintextStream;
    private IRandomAccessStream? _randomAccessStream;
    private MediaSource? _mediaSource;
    private CancellationTokenSource? _imageLoad;
    private bool _cleanedUp;
    private bool? _muteBeforeBossKey;
    private readonly List<VaultItem> _queue = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly DispatcherTimer _loadingDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private VaultViewState? _returnState;
    private int _index = -1;
    private int _loadVersion;
    private bool _showingImage;
    private bool _fullScreen;
    private bool _deleting;
    private bool _preserveErrorBanner;
    private double _volume = 1;
    private bool _userMuted;
    private double _speed = 1;
    private static string? _lastItemId;
    private VaultItem? CurrentItem => _index >= 0 && _index < _queue.Count ? _queue[_index] : null;
    internal string? CurrentMediaId => CurrentItem?.Id;
    internal bool IsImageReady => !_cleanedUp && ImageContainer.Visibility == Visibility.Visible && ImageViewer.Source != null;
    internal bool IsMediaReady { get; private set; }
    internal string? MediaError { get; private set; }
    internal double PlaybackSeconds => _mediaPlayer?.PlaybackSession.Position.TotalSeconds ?? 0;
    internal int ImageCount => _showingImage ? _queue.Count : 0;
    internal (bool Previous, bool Next) NavigationEnabled => (PreviousButton.IsEnabled, NextButton.IsEnabled);
    internal float ImageZoom => ImageContainer.ZoomFactor;
    internal Task<byte[]> CaptureImagePixelsAsync() => UiCapture.PixelsAsync(ImageViewer);
    internal Task<byte[]> CaptureImageViewportPixelsAsync() => UiCapture.PixelsAsync(ImageContainer);
    internal void ReturnToGallery() => BackBtn_Click(this, new RoutedEventArgs());
    internal UIElement NavigationChrome => PlayerTopBar;

    static PlayerPage()
    {
        // The ID must also be forgotten when the user locks or changes spaces on another page.
        VaultService.Instance.Locked += ForgetLastItem;
        VaultService.Instance.Unlocked += ForgetLastItem;
    }

    private static void ForgetLastItem() => _lastItemId = null;

    public PlayerPage()
    {
        InitializeComponent();
        PlayerElement.AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler(PlayerElement_DoubleTapped), true);
        PlayerElement.RegisterPropertyChangedCallback(MediaPlayerElement.IsFullWindowProperty, (_, _) =>
        {
            if (!PlayerElement.IsFullWindow) return;
            PlayerElement.IsFullWindow = false;
            if (CanUseControls && CurrentItem?.MediaType == "Video")
            {
                SetPlayerFullScreen(!_fullScreen);
            }
        });

        _loadingDelay.Tick += (_, _) =>
        {
            _loadingDelay.Stop();
            if (_cleanedUp || !VaultService.Instance.IsUnlocked) return;
            LoadingRing.IsActive = UiMotion.AnimationsEnabled;
            LoadingRing.Visibility = LoadingRing.IsActive ? Visibility.Visible : Visibility.Collapsed;
            UiMotion.SetVisible(LoadingFeedback, true, MotionCue.Fade);
        };
        Unloaded += PlayerPage_Unloaded;
        PrivacyMediaGuard.AppAudioMuteChanged += OnAppAudioMuteChanged;
        VaultService.Instance.Locked += OnVaultLocked;
        VaultWork.Changed += OnVaultWorkChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var request = e.Parameter as MediaOpenRequest;
        var vault = VaultService.Instance;
        string? id = (request?.Item ?? e.Parameter as VaultItem)?.Id ?? _lastItemId;
        VaultItem? item = vault.IsUnlocked && id != null ? vault.VisibleItems.FirstOrDefault(entry => entry.Id == id) : null;
        if (item == null)
        {
            ShowEmptyState();
            return;
        }
        _returnState = request?.ReturnState;
        IEnumerable<VaultItem> items = request?.Items is { } displayed ? displayed : vault.VisibleItems.OrderByDescending(entry => entry.ImportedAt);
        _queue.AddRange(items.Where(entry => entry.MediaType == item.MediaType).DistinctBy(entry => entry.Id));
        _index = _queue.FindIndex(entry => entry.Id == item.Id);
        if (_index < 0) { _index = _queue.Count; _queue.Add(item); }
        await LoadMediaAsync(item);
    }

    private void ShowEmptyState()
    {
        ++_loadVersion;
        _imageLoad?.Cancel();
        ReleasePlayer();
        ReleaseImage();
        SetPlayerFullScreen(false);
        _lastItemId = null;
        MediaTitleText.Text = "未选择媒体";
        ToolTipService.SetToolTip(MediaTitleText, null);
        MediaFormatBadge.Text = "";
        PlayerEmptyNotice.Visibility = Visibility.Visible;
        PlayerElement.Visibility = ImageContainer.Visibility = NavigationBar.Visibility = Visibility.Collapsed;
        ImageFitButton.Visibility = RotateButton.Visibility = DeleteButton.Visibility = Visibility.Collapsed;
        SpeedButton.Visibility = StretchButton.Visibility = FullScreenButton.Visibility = Visibility.Collapsed;
        PlaybackInfoBar.IsOpen = false;
        EndLoadingFeedback();
    }

    private MediaPlayer CreateMediaPlayer()
    {
        var player = new MediaPlayer { Volume = _volume, IsMuted = _userMuted };
        try { player.CommandManager.IsEnabled = false; } catch { }
        try { player.SystemMediaTransportControls.IsEnabled = false; } catch { }
        player.MediaFailed += OnMediaFailed;
        player.MediaOpened += OnMediaOpened;
        _mediaPlayer = player;
        try
        {
            PlayerElement.SetMediaPlayer(player);
            ApplyAppAudioMute(player);
            if (PlayerElement.TransportControls != null)
            {
                PlayerElement.TransportControls.IsCompact = false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("初始化媒体播放器失败", ex);
            throw;
        }

        return player;
    }

    private void ReleaseImage()
    {
        StopImageDrag();
        _webpPlayback?.Cancel();
        if (ImageViewer.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage animation) animation.Stop();
        var previous = ImageViewer.Source as IDisposable;
        ImageViewer.Source = null;
        previous?.Dispose();
    }

    private async Task LoadMediaAsync(VaultItem item, int direction = 0)
    {
        PlayerEmptyNotice.Visibility = Visibility.Collapsed;
        _imageLoad?.Cancel();
        int version = ++_loadVersion;
        _preserveErrorBanner = false;
        ReleasePlayer();
        _lastItemId = item.Id;
        ImageRotation.Angle = 0;
        RotateButton.IsEnabled = false;
        MediaError = null;
        MediaTitleText.Text = item.OriginalFileName;
        ToolTipService.SetToolTip(MediaTitleText, item.OriginalFileName);
        MediaFormatBadge.Text = (item.FileExtension ?? string.Empty).TrimStart('.').ToUpperInvariant();
        if (!_loadingDelay.IsEnabled && LoadingFeedback.Visibility != Visibility.Visible)
            _loadingDelay.Start();
        PlaybackInfoBar.IsOpen = false;
        PlayerElement.Visibility = Visibility.Collapsed;
        bool image = item.MediaType == "Image";
        PlaybackInfoBar.Title = image ? "无法显示图片" : "无法播放";
        _showingImage = image;
        ReleaseImage();
        // Leave the reading surface in place between photos, keeping its focus and
        // measured viewport while the old plaintext pixels are released immediately.
        ImageContainer.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
        NavigationBar.Visibility = _fullScreen ? Visibility.Collapsed : Visibility.Visible;
        NavigationHint.Text = image ? "← / → 切换图片" : item.MediaType is "Audio" or "Video" ? "← / → 跳转 5 秒　↑ / ↓ 音量" : "";
        UpdateNavigation();
        SpeedButton.Visibility = item.MediaType is "Audio" or "Video" ? Visibility.Visible : Visibility.Collapsed;
        StretchButton.Visibility = FullScreenButton.Visibility = item.MediaType == "Video" ? Visibility.Visible : Visibility.Collapsed;
        ImageFitButton.Visibility = ImageZoomControls.Visibility = RotateButton.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Visibility = Visibility.Visible;
        UpdateDeleteButton();

        try
        {
            if (item.MediaType == "Image")
            {
                using var imageLoad = new CancellationTokenSource();
                _imageLoad = imageLoad;
                VaultImageDecoder.DecodedImage? bitmap = null;
                Microsoft.UI.Xaml.Media.Imaging.BitmapImage? animatedBitmap = null;
                bool animateWebp = false;
                int animatedWidth = 0;
                int animatedHeight = 0;
                try
                {
                    await _loadGate.WaitAsync(imageLoad.Token);
                    try
                    {
                        string ext = (item.FileExtension ?? string.Empty).ToLowerInvariant();
                        if (ext is ".gif" or ".webp")
                        {
                            using Stream decrypted = await Task.Run(() => VaultService.Instance.OpenDecryptedStream(item), imageLoad.Token);
                            using IRandomAccessStream random = decrypted.AsRandomAccessStream();
                            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(random).AsTask(imageLoad.Token);
                            animateWebp = ext == ".webp" && decoder.FrameCount > 1;
                            if (ext == ".gif" && decoder.FrameCount > 1)
                            {
                                random.Seek(0);
                                var size = VaultImageDecoder.GetDecodeSize(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight, 16384, 16 * 1024 * 1024);
                                var anim = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
                                {
                                    AutoPlay = true, DecodePixelWidth = (int)size.Width, DecodePixelHeight = (int)size.Height
                                };
                                await anim.SetSourceAsync(random).AsTask(imageLoad.Token);
                                animatedBitmap = anim;
                                animatedWidth = (int)size.Width;
                                animatedHeight = (int)size.Height;
                            }
                        }

                        if (animatedBitmap == null)
                        {
                            bitmap = await VaultImageDecoder.DecodeAsync(item, 16384, imageLoad.Token);
                        }
                    }
                    finally { _loadGate.Release(); }
                }
                finally
                {
                    if (ReferenceEquals(_imageLoad, imageLoad)) _imageLoad = null;
                }
                if (_cleanedUp || version != _loadVersion || imageLoad.IsCancellationRequested || !VaultService.Instance.IsUnlocked)
                {
                    animatedBitmap?.Stop();
                    bitmap?.Source.Dispose();
                    return;
                }
                _imagePixelWidth = animatedBitmap != null ? animatedWidth : bitmap!.Value.Width;
                _imagePixelHeight = animatedBitmap != null ? animatedHeight : bitmap!.Value.Height;
                _imageWidthMode = _preferredWidthMode ?? _imagePixelHeight >= _imagePixelWidth * 2.0;
                ImageViewer.Source = (ImageSource?)animatedBitmap ?? bitmap!.Value.Source;
                if (animateWebp) _ = PlayWebpAsync(item, version, (uint)_imagePixelWidth, (uint)_imagePixelHeight);
                RotateButton.IsEnabled = true;
                ApplyImageLayout(resetViewport: true);
                if (direction == 0 && XamlRoot is { } root &&
                    FocusManager.GetFocusedElement(root) is not Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
                    ImageContainer.Focus(FocusState.Programmatic);
                UiMotion.Reveal(ImageViewer, direction switch
                {
                    < 0 => MotionCue.FromLeft,
                    > 0 => MotionCue.FromRight,
                    _ => MotionCue.Fade
                });
                return;
            }

            if (item.MediaType is not ("Video" or "Audio"))
                throw new NotSupportedException("此文件已加密保存，但不支持内置预览。请从媒体库导出副本后打开。");

            // Video/audio: decrypt chunks on demand through a seekable stream so 4K files
            // never have to fit in RAM (the old implementation threw OutOfMemoryException).
            await _loadGate.WaitAsync();
            try
            {
                if (version != _loadVersion) return;
                Stream stream = await Task.Run(() => VaultService.Instance.OpenDecryptedStream(item));
                if (version != _loadVersion || !VaultService.Instance.IsUnlocked)
                {
                    stream.Dispose();
                    return;
                }
                _plaintextStream = stream;
                _randomAccessStream = _plaintextStream.AsRandomAccessStream();
                _mediaSource = MediaSource.CreateFromStream(_randomAccessStream, item.MimeType);
                MediaPlayer player = CreateMediaPlayer();
                player.Source = _mediaSource;
                PlayerElement.Visibility = Visibility.Visible;
                player.Play();
            }
            finally { _loadGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_cleanedUp || version != _loadVersion) return;
            ReleasePlayer();
            AppLog.Error("解密/加载媒体失败", ex);
            if (image) ImageContainer.Visibility = Visibility.Collapsed;
            ShowPlaybackError(item.MediaType == "Image"
                ? "图片可能已损坏，或 Windows 没有安装此格式的解码器。"
                : "解密或加载失败：" + ex.Message);
        }
        finally
        {
            if (version == _loadVersion && (image || PlaybackInfoBar.IsOpen || _cleanedUp)) EndLoadingFeedback();
        }
    }

    private void EndLoadingFeedback()
    {
        _loadingDelay.Stop();
        LoadingRing.IsActive = false;
        UiMotion.SetVisible(LoadingFeedback, false);
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_cleanedUp || !ReferenceEquals(sender, _mediaPlayer)) return;
            IsMediaReady = true;
            if (_speed != 1)
            {
                try { sender.PlaybackSession.PlaybackRate = _speed; }
                catch (Exception ex) { AppLog.Warn("恢复倍速失败", ex); }
            }
            SpeedButton.IsEnabled = true;
            if (!_preserveErrorBanner)
            {
                PlaybackInfoBar.IsOpen = false;
            }
            EndLoadingFeedback();
        });
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_cleanedUp || !ReferenceEquals(sender, _mediaPlayer)) return;
            IsMediaReady = false;
            SpeedButton.IsEnabled = false;
            EndLoadingFeedback();
            string hint = args.Error == MediaPlayerError.SourceNotSupported
                ? "\n\n该系统缺少此格式的解码器（例如 MKV/HEVC/AV1）。可安装系统扩展后重试，或导出后使用支持该格式的本地播放器。"
                : string.Empty;
            ShowPlaybackError($"播放失败（{args.Error}）：{args.ErrorMessage}{hint}");
        });
    }

    private void ShowPlaybackError(string message)
    {
        MediaError = message;
        PlaybackInfoBar.Message = message;
        UiMotion.Open(PlaybackInfoBar);
    }

    private void ImageFit_Click(object sender, RoutedEventArgs e) => FitImageToWindow();

    internal void FitImageToWidth()
    {
        if (!IsImageReady) return;
        _imageWidthMode = true;
        _preferredWidthMode = true;
        ApplyImageLayout(resetViewport: true);
    }

    private void ImageWidthFit_Click(object sender, RoutedEventArgs e) => FitImageToWidth();

    private void UpdateNavigation()
    {
        PositionText.Text = _index >= 0 ? $"{_index + 1} / {_queue.Count}" : "";
        PreviousButton.IsEnabled = !_deleting && _index > 0;
        NextButton.IsEnabled = !_deleting && _index >= 0 && _index < _queue.Count - 1;
    }

    private void UpdateDeleteButton() =>
        DeleteButton.IsEnabled = !_deleting && !_cleanedUp && VaultService.Instance.IsUnlocked && !VaultWork.IsBusy && CurrentItem != null;

    private async void OnVaultWorkChanged()
    {
        UpdateDeleteButton();
        if (!_cleanedUp && !_deleting && VaultService.Instance.IsUnlocked && !VaultWork.IsBusy) await RefreshQueueAsync();
    }

    private async Task RefreshQueueAsync()
    {
        if (CurrentItem is not { } previous) return;
        var available = VaultService.Instance.VisibleItems.ToDictionary(item => item.Id);
        // Removals may have completed while the user was watching a different item.
        int before = _queue.Take(_index).Count(item => available.ContainsKey(item.Id));
        _queue.RemoveAll(item => !available.ContainsKey(item.Id));
        for (int i = 0; i < _queue.Count; i++) _queue[i] = available[_queue[i].Id];
        _index = Math.Min(before, _queue.Count - 1);
        UpdateNavigation();
        if (CurrentItem is not { } next) { ShowEmptyState(); return; }
        if (previous.Id != next.Id) { await LoadMediaAsync(next); return; }
        MediaTitleText.Text = next.OriginalFileName;
        ToolTipService.SetToolTip(MediaTitleText, next.OriginalFileName);
    }

    private async void DeleteCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUseControls || VaultWork.IsBusy || XamlRoot == null || CurrentItem is not { } item) return;
        _deleting = true;
        UpdateDeleteButton();
        UpdateNavigation();
        bool released = false;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "删除当前项？",
                Content = $"将覆盖并删除“{item.OriginalFileName}”的加密文件，此操作不可恢复。",
                PrimaryButtonText = "粉碎删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            if (await WorkspaceDialogs.ShowAsync(dialog) != ContentDialogResult.Primary ||
                _cleanedUp || !ReferenceEquals(CurrentItem, item) || !VaultService.Instance.VisibleItems.Contains(item)) return;
            using var work = VaultWork.TryBegin("删除媒体");
            if (work == null) return;
            ++_loadVersion;
            _imageLoad?.Cancel();
            ReleasePlayer();
            ReleaseImage();
            released = true;
            // Wait until even a cancelled decoder or an opening video has closed its file handle.
            await _loadGate.WaitAsync(work.Token);
            _loadGate.Release();
            if (work.Token.IsCancellationRequested || _cleanedUp || !VaultService.Instance.VisibleItems.Contains(item)) return;
            await VaultService.Instance.DeleteItemsAsync([item], new Progress<double>(work.Report), work.Token);
            if (_cleanedUp || work.Token.IsCancellationRequested) return;
            if (VaultService.Instance.VisibleItems.Any(entry => entry.Id == item.Id))
                throw new InvalidOperationException("媒体记录已更新，请重新选择后再试。");
            await RefreshQueueAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_cleanedUp || !VaultService.Instance.IsUnlocked) return;
            if (released && CurrentItem == item && VaultService.Instance.VisibleItems.Contains(item)) await LoadMediaAsync(item);
            if (_cleanedUp) return;
            AppLog.Error("删除媒体失败", ex);
            _preserveErrorBanner = true;
            PlaybackInfoBar.Title = "无法删除";
            ShowPlaybackError(ex.Message);
        }
        finally
        {
            _deleting = false;
            if (!_cleanedUp) { UpdateNavigation(); UpdateDeleteButton(); }
        }
    }

    internal Task SelectMediaAsync(int index)
    {
        if (_cleanedUp || _deleting || !VaultService.Instance.IsUnlocked || index < 0 || index >= _queue.Count || index == _index)
            return Task.CompletedTask;
        int direction = Math.Sign(index - _index);
        _index = index;
        return LoadMediaAsync(_queue[index], direction);
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) => await SelectMediaAsync(_index - 1);
    private async void Next_Click(object sender, RoutedEventArgs e) => await SelectMediaAsync(_index + 1);

    private bool CanUseControls => !_cleanedUp && !_deleting && VaultService.Instance.IsUnlocked && MainWindow.Current?.IsPrivacyCoverVisible != true;

    private async void Navigation_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!CanUseControls || CurrentItem == null) return;
        if (HandlePlaybackKey(sender.Key)) { args.Handled = true; return; }
        int? index = sender.Key switch
        {
            VirtualKey.PageUp => _index - 1,
            VirtualKey.PageDown => _index + 1,
            VirtualKey.Left when _showingImage => _index - 1,
            VirtualKey.Right when _showingImage => _index + 1,
            VirtualKey.Home when _showingImage => 0,
            VirtualKey.End when _showingImage => _queue.Count - 1,
            _ => null
        };
        if (index == null) return;
        args.Handled = true;
        await SelectMediaAsync(index.Value);
    }

    private void Page_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!CanUseControls || HasModifier(VirtualKey.Control) || HasModifier(VirtualKey.Menu) || HasModifier(VirtualKey.Shift)) return;
        if (HandlePlaybackKey(e.Key)) e.Handled = true;
    }

    private static bool HasModifier(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    internal bool HandlePlaybackKey(VirtualKey key)
    {
        if (key == VirtualKey.Escape && PlayerElement.IsFullWindow) { PlayerElement.IsFullWindow = false; return true; }
        if (key == VirtualKey.Escape && _fullScreen) { SetPlayerFullScreen(false); return true; }
        if (key == VirtualKey.F && CurrentItem?.MediaType == "Video") { SetPlayerFullScreen(!_fullScreen); return true; }
        if (_mediaPlayer is not { } player || !IsMediaReady) return false;
        try
        {
            var session = player.PlaybackSession;
            switch (key)
            {
                case VirtualKey.Left:
                case VirtualKey.Right:
                    if (session.CanSeek && session.NaturalDuration > TimeSpan.Zero)
                    {
                        double seconds = session.Position.TotalSeconds + (key == VirtualKey.Left ? -5 : 5);
                        session.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, session.NaturalDuration.TotalSeconds));
                    }
                    return true;
                case VirtualKey.Up:
                case VirtualKey.Down:
                    player.Volume = Math.Clamp(player.Volume + (key == VirtualKey.Up ? 0.05 : -0.05), 0, 1);
                    return true;
            }
        }
        catch (Exception ex) { AppLog.Warn("调整播放状态失败", ex); }
        return false;
    }

    internal void ExitAnyFullWindow()
    {
        try { if (PlayerElement.IsFullWindow) PlayerElement.IsFullWindow = false; } catch { }
        if (_fullScreen) SetPlayerFullScreen(false);
    }

    private void SetPlayerFullScreen(bool enable)
    {
        try { if (!enable && PlayerElement.IsFullWindow) PlayerElement.IsFullWindow = false; } catch { }
        if (_fullScreen == enable || MainWindow.Current is not { } window) return;
        window.SetFullScreenMode(enable);
        _fullScreen = window.AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
        PlayerTopBar.Visibility = _fullScreen ? Visibility.Collapsed : Visibility.Visible;
        NavigationBar.Visibility = _fullScreen || CurrentItem == null ? Visibility.Collapsed : Visibility.Visible;
        ExitFullScreenButton.Visibility = _fullScreen ? Visibility.Visible : Visibility.Collapsed;
        if (_fullScreen) PlayerElement.Focus(FocusState.Programmatic);
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        if (CanUseControls) SetPlayerFullScreen(!_fullScreen);
    }

    private void PlayerElement_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!CanUseControls || CurrentItem?.MediaType != "Video") return;
        // A double-click on a transport button or the seek slider belongs to that control.
        for (var element = e.OriginalSource as DependencyObject; element != null && element != PlayerElement;
             element = VisualTreeHelper.GetParent(element))
            if (element is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or Slider) return;
        SetPlayerFullScreen(!_fullScreen);
        e.Handled = true;
    }

    private void ZoomImage(float zoom)
    {
        if (!IsImageReady) return;
        double ratio = zoom / ImageContainer.ZoomFactor;
        double x = (ImageContainer.HorizontalOffset + ImageContainer.ViewportWidth / 2) * ratio - ImageContainer.ViewportWidth / 2;
        double y = (ImageContainer.VerticalOffset + ImageContainer.ViewportHeight / 2) * ratio - ImageContainer.ViewportHeight / 2;
        ImageContainer.ChangeView(Math.Max(0, x), Math.Max(0, y), zoom, disableAnimation: !UiMotion.AnimationsEnabled);
    }
    private void ImageZoomIn_Click(object sender, RoutedEventArgs e) => ZoomImage(Math.Min(16, ImageContainer.ZoomFactor * 1.25f));
    private void ImageZoomOut_Click(object sender, RoutedEventArgs e) => ZoomImage(Math.Max(1, ImageContainer.ZoomFactor / 1.25f));
    private void ImageContainer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate) UpdateImageZoomLabel();
    }
    private void ImageViewer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ZoomImage(ImageContainer.ZoomFactor > 1.01f ? 1 : 2);
        e.Handled = true;
    }

    private void OnAppAudioMuteChanged(bool _)
    {
        void Update()
        {
            if (_mediaPlayer is not { } player) return;
            try { ApplyAppAudioMute(player); }
            catch (Exception ex)
            {
                AppLog.Warn($"更新播放器静音状态失败: {ex.GetType().Name}");
            }
        }

        if (DispatcherQueue.HasThreadAccess) Update();
        else DispatcherQueue.TryEnqueue(Update);
    }

    private void ApplyAppAudioMute(MediaPlayer player)
    {
        if (PrivacyMediaGuard.IsAppAudioMuted)
        {
            _muteBeforeBossKey ??= player.IsMuted;
            player.IsMuted = true;
        }
        else if (_muteBeforeBossKey is bool previous)
        {
            player.IsMuted = previous;
            _muteBeforeBossKey = null;
        }
    }

    private void OnVaultLocked()
    {
        // Vault key is gone: stop decoding immediately even if this page is not visible.
        if (DispatcherQueue.HasThreadAccess) Cleanup();
        else DispatcherQueue.TryEnqueue(Cleanup);
    }

    private void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item ||
            !double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double rate)) return;
        if (_mediaPlayer == null) return;

        try
        {
            _mediaPlayer.PlaybackSession.PlaybackRate = rate;
            _speed = rate;
            SpeedButton.Content = item.Text;
        }
        catch (Exception ex)
        {
            AppLog.Warn("设置倍速失败: " + ex.GetType().Name);
            if (SpeedButton.Flyout is MenuFlyout menu &&
                menu.Items.OfType<RadioMenuFlyoutItem>().FirstOrDefault(option => option.Text == SpeedButton.Content as string) is { } previous)
                previous.IsChecked = true;
        }
    }

    private void StretchToggle_Click(object sender, RoutedEventArgs e)
    {
        PlayerElement.Stretch = StretchButton.IsChecked == true ? Stretch.UniformToFill : Stretch.Uniform;
    }

    private void PlayerBossKey_Click(object sender, RoutedEventArgs e)
    {
        // Do not tear down playback here: a panic-minimise should be reversible.
        // Media stops instantly only when the workspace actually locks (VaultService.Locked).
        BossKeyService.Instance.TriggerBossKey();
    }

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!CanUseControls) return;
        args.Handled = true;
        if (PlayerElement.IsFullWindow) PlayerElement.IsFullWindow = false;
        else if (_fullScreen) SetPlayerFullScreen(false);
        else BackBtn_Click(sender, new RoutedEventArgs());
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!CanUseControls) return;
        if (e.Key == VirtualKey.Escape)
        {
            if (PlayerElement.IsFullWindow) PlayerElement.IsFullWindow = false;
            else if (_fullScreen) SetPlayerFullScreen(false);
            else BackBtn_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Space && _mediaPlayer != null)
        {
            try
            {
                if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                {
                    _mediaPlayer.Pause();
                }
                else
                {
                    _mediaPlayer.Play();
                }
                e.Handled = true;
            }
            catch
            {
                // ignored
            }
        }
    }

    private void OpenLibrary_Click(object sender, RoutedEventArgs e) => BackBtn_Click(sender, e);

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_deleting) return;
        var state = _returnState is { } previous ? previous with { SelectedId = CurrentItem?.Id } : null;
        Cleanup();
        MainWindow.Current?.ReturnToVault(state);
    }

    private void PlayerPage_Unloaded(object sender, RoutedEventArgs e)
    {
        PrivacyMediaGuard.AppAudioMuteChanged -= OnAppAudioMuteChanged;
        VaultService.Instance.Locked -= OnVaultLocked;
        VaultWork.Changed -= OnVaultWorkChanged;
        Cleanup();
    }

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        _loadVersion++;
        _queue.Clear();
        _index = -1;
        _returnState = null;
        SetPlayerFullScreen(false);
        ReleasePlayer();
        _imageLoad?.Cancel();
        try { ReleaseImage(); } catch { }
        EndLoadingFeedback();
    }

    private void ReleasePlayer()
    {
        IsMediaReady = false;
        SpeedButton.IsEnabled = false;
        try { if (PlayerElement.IsFullWindow) PlayerElement.IsFullWindow = false; } catch { }

        // Detach the player from the XAML element *before* disposing it: disposing a
        // MediaPlayer that a live MediaPlayerElement still references can fail-fast in
        // native COM/CoreMessaging instead of throwing catchable exceptions.
        MediaPlayer? player = _mediaPlayer;
        _mediaPlayer = null;

        if (player != null)
        {
            try { _volume = player.Volume; _userMuted = _muteBeforeBossKey ?? player.IsMuted; } catch { }
            try { PlayerElement.SetMediaPlayer(null); } catch { }
            try { player.Pause(); } catch { }
            try { player.Source = null; } catch { }
            try
            {
                player.MediaFailed -= OnMediaFailed;
                player.MediaOpened -= OnMediaOpened;
            }
            catch { }
            try { player.Dispose(); } catch { }
        }
        _muteBeforeBossKey = null;

        try { _mediaSource?.Dispose(); } catch { }
        _mediaSource = null;

        try { _randomAccessStream?.Dispose(); } catch { }
        _randomAccessStream = null;

        try { _plaintextStream?.Dispose(); } catch { }
        _plaintextStream = null;
    }
}
