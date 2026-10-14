using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Dipping.WPF.Views
{
    public partial class CameraImagePanel : UserControl
    {
        private const double ZoomFactor = 1.15;
        private const double MinScale = 0.25;
        private const double MaxScale = 16;

        private Point _panStart;
        private bool _panning;

        public CameraImagePanel()
        {
            InitializeComponent();
            ImageViewport.SizeChanged += (_, _) => UpdateZoomPercent();
        }

        private void PreviewImageView_OnTargetUpdated(object? sender, DataTransferEventArgs e)
        {
            UpdatePixelLayerSize();
            UpdateZoomPercent();
        }

        private void ImageViewport_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (PreviewImageView.Source == null)
                return;

            ZoomAt(e.GetPosition(ImageViewport), e.Delta > 0 ? ZoomFactor : 1.0 / ZoomFactor);
            e.Handled = true;
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e) =>
            ZoomAt(ViewportCenter(), 1.0 / ZoomFactor);

        private void ZoomIn_Click(object sender, RoutedEventArgs e) =>
            ZoomAt(ViewportCenter(), ZoomFactor);

        private void ZoomActual_Click(object sender, RoutedEventArgs e)
        {
            if (PreviewImageView.Source == null)
                return;

            var fit = GetFitScale();
            if (fit <= 0)
                return;

            var scale = Clamp(1.0 / fit, MinScale, MaxScale);
            ImageScale.ScaleX = scale;
            ImageScale.ScaleY = scale;
            ImageTranslate.X = ImageViewport.ActualWidth / 2 * (1 - scale);
            ImageTranslate.Y = ImageViewport.ActualHeight / 2 * (1 - scale);
            UpdateZoomPercent();
        }

        private void ZoomAt(Point cursor, double factor)
        {
            if (PreviewImageView.Source == null)
                return;

            var newScale = Clamp(ImageScale.ScaleX * factor, MinScale, MaxScale);
            if (Math.Abs(newScale - ImageScale.ScaleX) < 0.0001)
                return;

            var scale = ImageScale.ScaleX;
            var imageX = (cursor.X - ImageTranslate.X) / scale;
            var imageY = (cursor.Y - ImageTranslate.Y) / scale;

            ImageScale.ScaleX = newScale;
            ImageScale.ScaleY = newScale;
            ImageTranslate.X = cursor.X - imageX * newScale;
            ImageTranslate.Y = cursor.Y - imageY * newScale;
            UpdateZoomPercent();
        }

        private void ImageViewport_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (PreviewImageView.Source == null)
                return;

            if (e.ClickCount >= 2)
            {
                ResetView();
                e.Handled = true;
                return;
            }

            _panning = true;
            _panStart = e.GetPosition(ImageViewport);
            ImageViewport.CaptureMouse();
            ImageViewport.Cursor = Cursors.Hand;
            e.Handled = true;
        }

        private void ImageViewport_OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_panning)
                return;

            var pos = e.GetPosition(ImageViewport);
            ImageTranslate.X += pos.X - _panStart.X;
            ImageTranslate.Y += pos.Y - _panStart.Y;
            _panStart = pos;
        }

        private void ImageViewport_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => StopPanning();

        private void ImageViewport_OnLostMouseCapture(object sender, MouseEventArgs e) => StopPanning();

        private void StopPanning()
        {
            if (!_panning)
                return;

            _panning = false;
            if (ImageViewport.IsMouseCaptured)
                ImageViewport.ReleaseMouseCapture();
            ImageViewport.Cursor = Cursors.Cross;
        }

        private void ResetView()
        {
            ImageScale.ScaleX = 1;
            ImageScale.ScaleY = 1;
            ImageTranslate.X = 0;
            ImageTranslate.Y = 0;
            UpdateZoomPercent();
        }

        private Point ViewportCenter() =>
            new(ImageViewport.ActualWidth / 2, ImageViewport.ActualHeight / 2);

        private double GetFitScale()
        {
            if (PreviewImageView.Source is not BitmapSource bitmap
                || bitmap.PixelWidth <= 0
                || bitmap.PixelHeight <= 0)
                return 1;

            var viewW = ImageViewport.ActualWidth;
            var viewH = ImageViewport.ActualHeight;
            if (viewW <= 1 || viewH <= 1)
                return 1;

            return Math.Min(viewW / bitmap.PixelWidth, viewH / bitmap.PixelHeight);
        }

        private void UpdateZoomPercent()
        {
            if (PreviewImageView.Source == null)
            {
                ZoomPercentText.Text = "—";
                return;
            }

            ZoomPercentText.Text =
                $"{Math.Max(1, (int)Math.Round(ImageScale.ScaleX * GetFitScale() * 100))}%";
        }

        private void UpdatePixelLayerSize()
        {
            if (PreviewImageView.Source is BitmapSource bitmap
                && bitmap.PixelWidth > 0
                && bitmap.PixelHeight > 0)
            {
                PixelLayer.Width = bitmap.PixelWidth;
                PixelLayer.Height = bitmap.PixelHeight;
            }
        }

        private static double Clamp(double value, double min, double max) =>
            value < min ? min : value > max ? max : value;
    }
}
