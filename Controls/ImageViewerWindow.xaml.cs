using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KaneCode.Controls;

/// <summary>
/// A full-screen, reusable image viewer with mouse-wheel zoom and click-drag pan.
/// Open with <see cref="Open"/> to display a <see cref="BitmapSource"/> in a
/// maximized borderless window. Press Escape or click the close button to dismiss.
/// </summary>
public partial class ImageViewerWindow : Window
{
    private double _scale = 1.0;
    private double _translateX;
    private double _translateY;
    private Point _lastMousePos;
    private bool _isPanning;
    private BitmapSource? _source;
    private double _initialScale = 1.0;

    private const double MinScale = 0.1;
    private const double MaxScale = 20.0;
    private const double ZoomStep = 0.1;

    public ImageViewerWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Opens the image viewer with the given image source and title.
    /// </summary>
    /// <param name="source">The image to display.</param>
    /// <param name="title">Window title (shown nowhere since the window is borderless, but set for accessibility).</param>
    /// <param name="owner">Optional owner window for correct z-order.</param>
    public static void Open(BitmapSource source, string title = "Image Viewer", Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var viewer = new ImageViewerWindow
        {
            Title = title,
            Owner = owner,
            _source = source
        };

        viewer.ShowDialog();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_source is null)
        {
            return;
        }

        ViewerImage.Source = _source;

        // Compute initial scale to fit the image within the window, with padding
        double availableWidth = ActualWidth - 80;
        double availableHeight = ActualHeight - 80;

        if (_source.PixelWidth > 0 && _source.PixelHeight > 0)
        {
            double scaleX = availableWidth / _source.PixelWidth;
            double scaleY = availableHeight / _source.PixelHeight;
            _initialScale = Math.Min(scaleX, scaleY);
        }
        else
        {
            _initialScale = 1.0;
        }

        _scale = _initialScale;
        ApplyTransforms();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            return;
        }

        // Reset to fit
        if (e.Key == Key.F || e.Key == Key.D0)
        {
            ResetZoom();
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _lastMousePos = e.GetPosition(this);
        CaptureMouse();
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPanning = false;
        ReleaseMouseCapture();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        Point currentPos = e.GetPosition(this);
        Vector delta = currentPos - _lastMousePos;

        _translateX += delta.X;
        _translateY += delta.Y;
        _lastMousePos = currentPos;

        ApplyTransforms();
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        Point mousePos = e.GetPosition(this);

        double oldScale = _scale;

        if (e.Delta > 0)
        {
            _scale = Math.Min(_scale + ZoomStep, MaxScale);
        }
        else
        {
            _scale = Math.Max(_scale - ZoomStep, MinScale);
        }

        // Adjust translation so the point under the cursor stays stationary
        double scaleChange = _scale / oldScale;
        _translateX = mousePos.X - scaleChange * (mousePos.X - _translateX);
        _translateY = mousePos.Y - scaleChange * (mousePos.Y - _translateY);

        ApplyTransforms();
    }

    private void ResetZoom()
    {
        _scale = _initialScale;
        _translateX = 0;
        _translateY = 0;
        ApplyTransforms();
    }

    private void ApplyTransforms()
    {
        ScaleXform.ScaleX = _scale;
        ScaleXform.ScaleY = _scale;
        TranslateXform.X = _translateX;
        TranslateXform.Y = _translateY;
    }
}
