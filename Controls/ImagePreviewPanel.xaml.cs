using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KaneCode.Controls;

/// <summary>
/// A zoomable/panable image preview pane for the editor. Shows raster images
/// (PNG, JPG, GIF, BMP, ICO, ...) loaded from disk, or renders SVG markup
/// passed as text. Supports mouse-wheel zoom (anchored at the cursor),
/// click-drag pan, double-click/fit-to-window, and a 1:1 actual-size view.
/// </summary>
public partial class ImagePreviewPanel : UserControl
{
    private const double MinScale = 0.05;
    private const double MaxScale = 20.0;
    private const double ZoomFactor = 1.12;
    private const double FitPadding = 24.0;

    private BitmapSource? _source;
    private double _scale = 1.0;
    private double _translateX;
    private double _translateY;
    private Point _lastMousePos;
    private bool _isPanning;

    public ImagePreviewPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Displays a raster image source and resets the view to fit.
    /// </summary>
    public void SetRasterImage(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        PreviewImage.Source = source;
        ErrorText.Visibility = Visibility.Collapsed;

        ResetView();
    }

    /// <summary>
    /// Renders SVG markup to a bitmap and displays it. Falls back to an error
    /// message if the SVG cannot be parsed or rendered.
    /// </summary>
    public void SetSvgContent(string? svgContent)
    {
        if (string.IsNullOrWhiteSpace(svgContent))
        {
            SetError("SVG content is empty.");
            return;
        }

        BitmapSource? bitmap = RenderSvg(svgContent);
        if (bitmap is null)
        {
            SetError("Could not render this SVG image.\nCheck that the markup is valid SVG.");
            return;
        }

        SetRasterImage(bitmap);
    }

    /// <summary>
    /// Clears the preview entirely.
    /// </summary>
    public void Clear()
    {
        _source = null;
        PreviewImage.Source = null;
        ErrorText.Visibility = Visibility.Collapsed;
        _scale = 1.0;
        _translateX = 0;
        _translateY = 0;
        ApplyTransforms();
        UpdateZoomIndicator();
    }

    /// <summary>
    /// Shows an error message in place of the image.
    /// </summary>
    public void SetError(string message)
    {
        _source = null;
        PreviewImage.Source = null;
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = Visibility.Visible;
        _scale = 1.0;
        _translateX = 0;
        _translateY = 0;
        ApplyTransforms();
        UpdateZoomIndicator();
    }

    private void ResetView()
    {
        _scale = 1.0;
        _translateX = 0;
        _translateY = 0;
        ApplyTransforms();

        // Wait for layout so ActualWidth/ActualHeight are available, then fit.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_source is not null && IsVisible)
            {
                FitToViewport();
            }
        }));
    }

    // ── View helpers ──────────────────────────────────────────────────

    /// <summary>
    /// The layout origin (top-left) of the image element inside the viewport
    /// before any transforms are applied. The image is centered, so this is
    /// the centering offset.
    /// </summary>
    private Point GetElementOrigin()
    {
        // The image is centered, so the origin is the centering offset. It can
        // be negative when the image is larger than the viewport.
        double left = (Viewport.ActualWidth - PreviewImage.ActualWidth) / 2.0;
        double top = (Viewport.ActualHeight - PreviewImage.ActualHeight) / 2.0;
        return new Point(left, top);
    }

    private void FitToViewport()
    {
        if (_source is null || Viewport.ActualWidth < 1 || Viewport.ActualHeight < 1)
        {
            return;
        }

        double scaleX = (Viewport.ActualWidth - FitPadding) / PreviewImage.ActualWidth;
        double scaleY = (Viewport.ActualHeight - FitPadding) / PreviewImage.ActualHeight;
        double fitScale = Math.Min(scaleX, scaleY);

        // Never upscale beyond 100% when fitting, like a typical image viewer.
        _scale = Math.Min(1.0, Math.Max(MinScale, fitScale));
        _translateX = 0;
        _translateY = 0;
        ApplyTransforms();
        UpdateZoomIndicator();
    }

    private void ApplyTransforms()
    {
        ScaleXform.ScaleX = _scale;
        ScaleXform.ScaleY = _scale;
        TranslateXform.X = _translateX;
        TranslateXform.Y = _translateY;
    }

    private void UpdateZoomIndicator()
    {
        ZoomText.Text = $"{Math.Round(_scale * 100)}%";
    }

    // ── Mouse interaction ─────────────────────────────────────────────

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_source is null)
        {
            return;
        }

        // Double-click resets the view to fit.
        if (e.ClickCount == 2)
        {
            FitToViewport();
            e.Handled = true;
            return;
        }

        _isPanning = true;
        _lastMousePos = e.GetPosition(Viewport);
        Viewport.CaptureMouse();
        Viewport.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPanning = false;
        Viewport.ReleaseMouseCapture();
        Viewport.Cursor = Cursors.Arrow;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        Point currentPos = e.GetPosition(Viewport);
        Vector delta = currentPos - _lastMousePos;

        _translateX += delta.X;
        _translateY += delta.Y;
        _lastMousePos = currentPos;

        ApplyTransforms();
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_source is null)
        {
            return;
        }

        Point mousePos = e.GetPosition(Viewport);
        double oldScale = _scale;
        double newScale = e.Delta > 0
            ? Math.Min(_scale * ZoomFactor, MaxScale)
            : Math.Max(_scale / ZoomFactor, MinScale);

        if (Math.Abs(newScale - oldScale) < 1e-9)
        {
            return;
        }

        // Keep the point under the cursor stationary while zooming.
        Point origin = GetElementOrigin();
        double px = (mousePos.X - origin.X - _translateX) / oldScale;
        double py = (mousePos.Y - origin.Y - _translateY) / oldScale;

        _scale = newScale;
        _translateX = mousePos.X - origin.X - px * newScale;
        _translateY = mousePos.Y - origin.Y - py * newScale;

        ApplyTransforms();
        UpdateZoomIndicator();
        e.Handled = true;
    }

    private void FitButton_Click(object sender, RoutedEventArgs e)
    {
        FitToViewport();
    }

    private void ActualSizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null)
        {
            return;
        }

        _scale = 1.0;
        _translateX = 0;
        _translateY = 0;
        ApplyTransforms();
        UpdateZoomIndicator();
    }

    // ── SVG rendering ─────────────────────────────────────────────────

    private static BitmapSource? RenderSvg(string svgContent)
    {
        try
        {
            using var svgStream = new MemoryStream(Encoding.UTF8.GetBytes(svgContent));
            Svg.SvgDocument svgDoc = Svg.SvgDocument.Open<Svg.SvgDocument>(svgStream);

            // Render at a decent base resolution so zooming stays crisp.
            using System.Drawing.Bitmap bitmap = svgDoc.Draw(1920, 0);

            IntPtr hBitmap = bitmap.GetHbitmap();
            try
            {
                return Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                NativeMethods.DeleteObject(hBitmap);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr hObject);
    }
}
