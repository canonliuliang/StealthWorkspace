using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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
    private bool _audioMuted;
    private readonly List<VaultItem> _images = new();
    private readonly SemaphoreSlim _imageDecodeGate = new(1, 1);
    private VaultViewState? _returnState;
    private int _imageIndex = -1;
    private int _loadVersion;
    private bool _showingImage;
    internal string? CurrentImageId => _showingImage && _imageIndex >= 0 && _imageIndex < _images.Count ? _images[_imageIndex].Id : null;
    internal bool IsImageReady => !_cleanedUp && ImageContainer.Visibility == Visibility.Visible && ImageViewer.Source != null;
    internal bool IsMediaReady { get; private set; }
    internal string? MediaError { get; private set; }
    internal double PlaybackSeconds => _mediaPlayer?.PlaybackSession.Position.TotalSeconds ?? 0;
    internal int ImageCount => _images.Count;
    internal (bool Previous, bool Next) ImageNavigationEnabled => (PreviousImageButton.IsEnabled, NextImageButton.IsEnabled);
    internal float ImageZoom => ImageContainer.ZoomFactor;
    internal Task<byte[]> CaptureImagePixelsAsync() => UiCapture.PixelsAsync(ImageViewer);
    internal Task<byte[]> CaptureImageViewportPixelsAsync() => UiCapture.PixelsAsync(ImageContainer);
    internal void ReturnToGallery() => BackBtn_Click(this, new RoutedEventArgs());

    public PlayerPage()
    {
        InitializeComponent();

        Unloaded += PlayerPage_Unloaded;
        PrivacyMediaGuard.AppAudioMuteChanged += OnAppAudioMuteChanged;
        VaultService.Instance.Locked += OnVaultLocked;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var request = e.Parameter as MediaOpenRequest;
        VaultItem? item = request?.Item ?? e.Parameter as VaultItem;
        if (item != null)
        {
            _returnState = request?.ReturnState;
            if (item.MediaType == "Image")
            {
                _images.AddRange((request?.Images ?? VaultService.Instance.VisibleItems.ToArray()).Where(media => media.MediaType == "Image").DistinctBy(media => media.Id));
                _imageIndex = _images.FindIndex(media => media.Id == item.Id);
                if (_imageIndex < 0) { _images.Add(item); _imageIndex = _images.Count - 1; }
            }
            await LoadMediaAsync(item);
        }
        else
        {
            MediaTitleText.Text = "未选择媒体";
            MediaFormatBadge.Text = "";
            PlayerEmptyNotice.Visibility = Visibility.Visible;
            PlayerElement.Visibility = Visibility.Collapsed;
            SpeedButton.Visibility = StretchButton.Visibility = Visibility.Collapsed;
        }
    }

    private Task<MediaPlayer> GetOrCreateMediaPlayerAsync()
    {
        if (_mediaPlayer != null) return Task.FromResult(_mediaPlayer);

        var player = new MediaPlayer();
        player.MediaFailed += OnMediaFailed;
        player.MediaOpened += OnMediaOpened;
        player.IsMuted = _audioMuted || PrivacyMediaGuard.IsAppAudioMuted;

        _mediaPlayer = player;
        try
        {
            PlayerElement.SetMediaPlayer(player);
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

        return Task.FromResult(player);
    }

    private void ReleaseImage()
    {
        StopImageDrag();
        var previous = ImageViewer.Source as IDisposable;
        ImageViewer.Source = null;
        previous?.Dispose();
    }

    private async Task LoadMediaAsync(VaultItem item, int direction = 0)
    {
        PlayerEmptyNotice.Visibility = Visibility.Collapsed;
        _imageLoad?.Cancel();
        int version = ++_loadVersion;
        _cleanedUp = false;
        MediaError = null;
        MediaTitleText.Text = item.OriginalFileName;
        ToolTipService.SetToolTip(MediaTitleText, item.OriginalFileName);
        MediaFormatBadge.Text = (item.FileExtension ?? string.Empty).TrimStart('.').ToUpperInvariant();
        LoadingRing.IsActive = true;
        PlaybackInfoBar.IsOpen = false;
        PlayerElement.Visibility = Visibility.Collapsed;
        ImageContainer.Visibility = Visibility.Collapsed;
        bool image = item.MediaType == "Image";
        _showingImage = image;
        ReleaseImage();
        ImageContainer.ChangeView(0, 0, 1, disableAnimation: true);
        ImageNavigationBar.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
        UpdateImageNavigation();
        SpeedButton.Visibility = StretchButton.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
        ImageFitButton.Visibility = image ? Visibility.Visible : Visibility.Collapsed;

        try
        {
            if (item.MediaType == "Image")
            {
                using var imageLoad = new CancellationTokenSource();
                _imageLoad = imageLoad;
                VaultImageDecoder.DecodedImage bitmap;
                try
                {
                    await _imageDecodeGate.WaitAsync(imageLoad.Token);
                    try { bitmap = await VaultImageDecoder.DecodeAsync(item, 16384, imageLoad.Token); }
                    finally { _imageDecodeGate.Release(); }
                }
                finally
                {
                    if (ReferenceEquals(_imageLoad, imageLoad)) _imageLoad = null;
                }
                if (_cleanedUp || version != _loadVersion || imageLoad.IsCancellationRequested || !VaultService.Instance.IsUnlocked)
                {
                    bitmap.Source.Dispose();
                    return;
                }
                _imagePixelWidth = bitmap.Width;
                _imagePixelHeight = bitmap.Height;
                _imageWidthMode = _preferredWidthMode ?? bitmap.Height >= bitmap.Width * 2.0;
                ImageViewer.Source = bitmap.Source;
                ImageContainer.Visibility = Visibility.Visible;
                ApplyImageLayout(resetViewport: true);
                ImageContainer.Focus(FocusState.Programmatic);
                UiMotion.Enter(ImageViewer, direction * 8, 0);
                return;
            }

            if (item.MediaType == "Document")
                throw new NotSupportedException("此文件已加密保存，但不支持内置预览。请从媒体库导出副本后打开。");

            // Video/audio: decrypt chunks on demand through a seekable stream so 4K files
            // never have to fit in RAM (the old implementation threw OutOfMemoryException).
            MediaPlayer player = await GetOrCreateMediaPlayerAsync();
            Stream stream = await Task.Run(() => VaultService.Instance.OpenDecryptedStream(item));
            if (_cleanedUp || !VaultService.Instance.IsUnlocked)
            {
                stream.Dispose();
                return;
            }
            _plaintextStream = stream;
            _randomAccessStream = _plaintextStream.AsRandomAccessStream();
            _mediaSource = MediaSource.CreateFromStream(_randomAccessStream, GetMimeType(item.FileExtension));
            player.Source = _mediaSource;
            PlayerElement.Visibility = Visibility.Visible;
            player.Play();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_cleanedUp || version != _loadVersion) return;
            AppLog.Error("解密/加载媒体失败", ex);
            PlaybackInfoBar.Title = item.MediaType == "Image" ? "无法显示图片" : "无法播放";
            ShowPlaybackError(item.MediaType == "Image"
                ? "图片可能已损坏，或 Windows 没有安装此格式的解码器。"
                : "解密或加载失败：" + ex.Message);
        }
        finally
        {
            if (version == _loadVersion && (item.MediaType == "Image" || PlaybackInfoBar.IsOpen || _cleanedUp)) LoadingRing.IsActive = false;
        }
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_cleanedUp || !ReferenceEquals(sender, _mediaPlayer)) return;
            IsMediaReady = true;
            PlaybackInfoBar.IsOpen = false;
            LoadingRing.IsActive = false;
        });
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_cleanedUp || !ReferenceEquals(sender, _mediaPlayer)) return;
            LoadingRing.IsActive = false;
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

    private void UpdateImageNavigation()
    {
        ImagePositionText.Text = _imageIndex >= 0 ? $"{_imageIndex + 1} / {_images.Count}" : "";
        PreviousImageButton.IsEnabled = _showingImage && _imageIndex > 0;
        NextImageButton.IsEnabled = _showingImage && _imageIndex >= 0 && _imageIndex < _images.Count - 1;
    }

    internal Task SelectImageAsync(int index)
    {
        if (_cleanedUp || !_showingImage || !VaultService.Instance.IsUnlocked || index < 0 || index >= _images.Count || index == _imageIndex) return Task.CompletedTask;
        int direction = Math.Sign(index - _imageIndex);
        _imageIndex = index;
        return LoadMediaAsync(_images[index], direction);
    }

    private async void PreviousImage_Click(object sender, RoutedEventArgs e) => await SelectImageAsync(_imageIndex - 1);
    private async void NextImage_Click(object sender, RoutedEventArgs e) => await SelectImageAsync(_imageIndex + 1);

    private async void ImageNavigation_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_showingImage || _cleanedUp) return;
        args.Handled = true;
        int index = sender.Key switch { VirtualKey.Left => _imageIndex - 1, VirtualKey.Right => _imageIndex + 1, VirtualKey.Home => 0, VirtualKey.End => _images.Count - 1, _ => _imageIndex };
        await SelectImageAsync(index);
    }

    private void ZoomImage(float zoom)
    {
        double ratio = zoom / ImageContainer.ZoomFactor;
        double x = (ImageContainer.HorizontalOffset + ImageContainer.ViewportWidth / 2) * ratio - ImageContainer.ViewportWidth / 2;
        double y = (ImageContainer.VerticalOffset + ImageContainer.ViewportHeight / 2) * ratio - ImageContainer.ViewportHeight / 2;
        ImageContainer.ChangeView(Math.Max(0, x), Math.Max(0, y), zoom, disableAnimation: !UiMotion.AnimationsEnabled);
    }
    private void ImageZoomIn_Click(object sender, RoutedEventArgs e) => ZoomImage(Math.Min(16, ImageContainer.ZoomFactor * 1.25f));
    private void ImageZoomOut_Click(object sender, RoutedEventArgs e) => ZoomImage(Math.Max(1, ImageContainer.ZoomFactor / 1.25f));
    private void ImageContainer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateImageZoomLabel();
    }
    private void ImageViewer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ZoomImage(ImageContainer.ZoomFactor > 1.01f ? 1 : 2);
        e.Handled = true;
    }

    private void OnAppAudioMuteChanged(bool muted)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _audioMuted = muted;
            if (_mediaPlayer != null)
            {
                try { _mediaPlayer.IsMuted = muted; } catch { }
            }
        });
    }

    private void OnVaultLocked()
    {
        // Vault key is gone: stop decoding immediately even if this page is not visible.
        DispatcherQueue.TryEnqueue(Cleanup);
    }

    private void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || !double.TryParse(item.Tag?.ToString(), out double rate)) return;
        if (_mediaPlayer == null) return;

        try
        {
            _mediaPlayer.PlaybackSession.PlaybackRate = rate;
            SpeedButton.Content = $"{rate:0.0#}x";
        }
        catch (Exception ex)
        {
            AppLog.Warn("设置倍速失败: " + ex.GetType().Name);
        }
    }

    private void StretchToggle_Click(object sender, RoutedEventArgs e)
    {
        PlayerElement.Stretch = PlayerElement.Stretch == Stretch.Uniform ? Stretch.UniformToFill : Stretch.Uniform;
    }

    private void PlayerBossKey_Click(object sender, RoutedEventArgs e)
    {
        // Do not tear down playback here: a panic-minimise should be reversible.
        // Media stops instantly only when the workspace actually locks (VaultService.Locked).
        BossKeyService.Instance.TriggerBossKey();
    }

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        BackBtn_Click(sender, new RoutedEventArgs());
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            BackBtn_Click(sender, new RoutedEventArgs());
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

    private static string GetMimeType(string? ext) => (ext ?? string.Empty).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        ".wmv" => "video/x-ms-wmv",
        ".ts" => "video/mp2t",
        ".mp3" => "audio/mpeg",
        ".m4a" or ".aac" => "audio/mp4",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        _ => "video/mp4"
    };

    private void OpenLibrary_Click(object sender, RoutedEventArgs e) => BackBtn_Click(sender, e);

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        var state = _returnState is { } previous ? previous with { SelectedId = CurrentImageId ?? previous.SelectedId } : null;
        Cleanup();
        MainWindow.Current?.ReturnToVault(state);
    }

    private void PlayerPage_Unloaded(object sender, RoutedEventArgs e)
    {
        PrivacyMediaGuard.AppAudioMuteChanged -= OnAppAudioMuteChanged;
        VaultService.Instance.Locked -= OnVaultLocked;
        Cleanup();
    }

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        _loadVersion++;
        _images.Clear();
        _imageIndex = -1;
        _returnState = null;
        IsMediaReady = false;

        // Detach the player from the XAML element *before* disposing it: disposing a
        // MediaPlayer that a live MediaPlayerElement still references can fail-fast in
        // native COM/CoreMessaging instead of throwing catchable exceptions.
        MediaPlayer? player = _mediaPlayer;
        _mediaPlayer = null;

        if (player != null)
        {
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

        try { _mediaSource?.Dispose(); } catch { }
        _mediaSource = null;

        try { _randomAccessStream?.Dispose(); } catch { }
        _randomAccessStream = null;

        try { _plaintextStream?.Dispose(); } catch { }
        _plaintextStream = null;

        _imageLoad?.Cancel();

        try { ReleaseImage(); } catch { }
        try { LoadingRing.IsActive = false; } catch { }
    }
}
