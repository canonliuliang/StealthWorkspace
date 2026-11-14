using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace StealthWorkspace.Pages;

public sealed partial class PlayerPage
{
    private int _imagePixelWidth;
    private int _imagePixelHeight;
    private bool _imageWidthMode;
    private bool? _preferredWidthMode;
    private bool _updatingImageLayout;
    private uint? _imageDragPointer;
    private Point _imageDragStart;
    private double _imageDragHorizontal;
    private double _imageDragVertical;
    internal bool IsWidthReading => _imageWidthMode && ImageContainer.ScrollableHeight > 0;
    internal double ImageDisplayedWidth => ImageViewer.ActualWidth;
    internal double ImageViewportWidth => ImageContainer.ViewportWidth;
    internal (int Width, int Height) ImageDecodedSize => (_imagePixelWidth, _imagePixelHeight);

    internal void FitImageToWindow()
    {
        if (!IsImageReady) return;
        _imageWidthMode = false;
        _preferredWidthMode = false;
        ApplyImageLayout(resetViewport: true);
    }

    private void ApplyImageLayout(bool resetViewport)
    {
        if (_updatingImageLayout || !IsImageReady || _imagePixelWidth <= 0 || _imagePixelHeight <= 0) return;
        double viewportWidth = ImageContainer.ActualWidth;
        double viewportHeight = ImageContainer.ActualHeight;
        if (viewportWidth <= 24 || viewportHeight <= 24) return;
        _updatingImageLayout = true;
        try
        {
            StopImageDrag();
            double verticalFraction = ImageContainer.VerticalOffset / Math.Max(1, ImageStage.Height * ImageContainer.ZoomFactor);
            double scale = (viewportWidth - 24) / _imagePixelWidth;
            if (!_imageWidthMode) scale = Math.Min(scale, (viewportHeight - 24) / _imagePixelHeight);
            double width = _imagePixelWidth * scale;
            double height = _imagePixelHeight * scale;
            ImageStage.Width = viewportWidth;
            ImageStage.Height = Math.Max(viewportHeight, height + 24);
            ImageViewer.Width = width;
            ImageViewer.Height = height;
            ImageViewer.VerticalAlignment = _imageWidthMode ? VerticalAlignment.Top : VerticalAlignment.Center;
            ImageContainer.UpdateLayout();
            if (resetViewport) ImageContainer.ChangeView(0, 0, 1, disableAnimation: true);
            else if (double.IsFinite(verticalFraction))
                ImageContainer.ChangeView(null, verticalFraction * ImageStage.Height * ImageContainer.ZoomFactor, null, disableAnimation: true);
            UpdateImageZoomLabel();
        }
        finally { _updatingImageLayout = false; }
    }

    private void ImageContainer_SizeChanged(object sender, SizeChangedEventArgs args) => ApplyImageLayout(resetViewport: false);

    private void UpdateImageZoomLabel()
    {
        if (ImageZoomResetButton != null)
            ImageZoomResetButton.Content = ImageContainer.ZoomFactor < 1.01f ? (_imageWidthMode ? "宽度" : "适应") : $"{ImageContainer.ZoomFactor:0.##}×";
    }

    private void ImageLayout_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsImageReady) return;
        args.Handled = true;
        if (sender.Key == VirtualKey.W) FitImageToWidth(); else FitImageToWindow();
    }

    private void ImageViewer_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!IsImageReady || args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse ||
            (ImageContainer.ScrollableHeight <= 0 && ImageContainer.ScrollableWidth <= 0)) return;
        var point = args.GetCurrentPoint(ImageContainer);
        if (!point.Properties.IsLeftButtonPressed || !ImageViewer.CapturePointer(args.Pointer)) return;
        _imageDragPointer = args.Pointer.PointerId;
        _imageDragStart = point.Position;
        _imageDragHorizontal = ImageContainer.HorizontalOffset;
        _imageDragVertical = ImageContainer.VerticalOffset;
        ImageContainer.Focus(FocusState.Pointer);
        args.Handled = true;
    }

    private void ImageViewer_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_imageDragPointer != args.Pointer.PointerId) return;
        var point = args.GetCurrentPoint(ImageContainer);
        if (!point.Properties.IsLeftButtonPressed) { StopImageDrag(); return; }
        ImageContainer.ChangeView(Math.Clamp(_imageDragHorizontal - point.Position.X + _imageDragStart.X, 0, ImageContainer.ScrollableWidth),
            Math.Clamp(_imageDragVertical - point.Position.Y + _imageDragStart.Y, 0, ImageContainer.ScrollableHeight), null, disableAnimation: true);
        args.Handled = true;
    }

    private void ImageViewer_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_imageDragPointer == args.Pointer.PointerId) StopImageDrag();
    }

    private void ImageViewer_PointerCaptureLost(object sender, PointerRoutedEventArgs args) => _imageDragPointer = null;

    private void StopImageDrag()
    {
        _imageDragPointer = null;
        ImageViewer.ReleasePointerCaptures();
    }
}
