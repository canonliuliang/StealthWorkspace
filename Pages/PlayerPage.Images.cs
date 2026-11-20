using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    internal double ImageDisplayedWidth => ImageBounds.ActualWidth;
    internal double ImageViewportWidth => ImageContainer.ViewportWidth;
    internal (int Width, int Height) ImageDecodedSize => (_imagePixelWidth, _imagePixelHeight);

    private void RotateImage_Click(object sender, RoutedEventArgs e) => RotateImage();

    internal void RotateImage()
    {
        if (!CanUseControls || !IsImageReady) return;
        ImageRotation.Angle = (ImageRotation.Angle + 90) % 360;
        ApplyImageLayout(resetViewport: true);
    }

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
            bool sideways = ImageRotation.Angle is 90 or 270;
            int pixelWidth = sideways ? _imagePixelHeight : _imagePixelWidth;
            int pixelHeight = sideways ? _imagePixelWidth : _imagePixelHeight;
            double scale = (viewportWidth - 24) / pixelWidth;
            if (!_imageWidthMode) scale = Math.Min(scale, (viewportHeight - 24) / pixelHeight);
            double width = pixelWidth * scale;
            double height = pixelHeight * scale;
            ImageStage.Width = viewportWidth;
            ImageStage.Height = Math.Max(viewportHeight, height + 24);
            ImageBounds.Width = width;
            ImageBounds.Height = height;
            ImageBounds.VerticalAlignment = _imageWidthMode ? VerticalAlignment.Top : VerticalAlignment.Center;
            ImageViewer.Width = _imagePixelWidth * scale;
            ImageViewer.Height = _imagePixelHeight * scale;
            Canvas.SetLeft(ImageViewer, (width - ImageViewer.Width) / 2);
            Canvas.SetTop(ImageViewer, (height - ImageViewer.Height) / 2);
            if (resetViewport) ImageContainer.ChangeView(0, 0, 1, disableAnimation: true);
            else if (ImageContainer.VerticalOffset > 0 && double.IsFinite(verticalFraction))
            {
                // A scrolled reader needs the new scroll extent to restore its
                // position. Ordinary resizes and image changes can use normal layout.
                ImageContainer.UpdateLayout();
                ImageContainer.ChangeView(null, verticalFraction * ImageStage.Height * ImageContainer.ZoomFactor, null, disableAnimation: true);
            }
            UpdateImageZoomLabel();
        }
        finally { _updatingImageLayout = false; }
    }

    private void ImageContainer_SizeChanged(object sender, SizeChangedEventArgs args) => ApplyImageLayout(resetViewport: false);

    private void UpdateImageZoomLabel()
    {
        if (ImageZoomResetButton == null) return;
        string label = ImageContainer.ZoomFactor < 1.01f ? (_imageWidthMode ? "宽度" : "适应") : $"{ImageContainer.ZoomFactor:0.##}×";
        if (!Equals(ImageZoomResetButton.Content, label)) ImageZoomResetButton.Content = label;
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
