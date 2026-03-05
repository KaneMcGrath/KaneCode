using KaneCode.Services.Ai;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace KaneCode.Controls;

/// <summary>
/// Overlay control that displays the current presentation slide with
/// navigation buttons (Back / Next) and a close button.
/// </summary>
public partial class PresentationOverlay : UserControl
{
    private PresentationService? _service;

    /// <summary>
    /// Raised when the user navigates to a slide and the editor needs to
    /// open the file and scroll to the target line.
    /// </summary>
    internal event EventHandler<PresentationSlide>? NavigateRequested;

    /// <summary>
    /// Raised when the user closes the presentation.
    /// </summary>
    internal event EventHandler? CloseRequested;

    public PresentationOverlay()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Binds this overlay to a <see cref="PresentationService"/> and begins
    /// listening for slide changes.
    /// </summary>
    internal void Bind(PresentationService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (_service is not null)
        {
            _service.SlideChanged -= OnSlideChanged;
        }

        _service = service;
        _service.SlideChanged += OnSlideChanged;
    }

    private void OnSlideChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(UpdateView);
    }

    private void UpdateView()
    {
        if (_service is null || !_service.IsActive || _service.CurrentSlide is null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;

        PresentationSlide slide = _service.CurrentSlide;
        int index = _service.CurrentIndex;
        int total = _service.Slides.Count;

        TitleText.Text = _service.Title ?? "Presentation";
        SlideText.Text = slide.Text;
        FileInfoText.Text = $"{Path.GetFileName(slide.FilePath)}  •  line {slide.Line}";
        SlideCounter.Text = $"{index + 1} / {total}";

        BackButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index < total - 1;

        NavigateRequested?.Invoke(this, slide);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _service?.MoveBack();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        _service?.MoveNext();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _service?.Close();
        Visibility = Visibility.Collapsed;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
