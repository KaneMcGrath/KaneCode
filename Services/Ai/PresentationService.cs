namespace KaneCode.Services.Ai;

/// <summary>
/// Represents a single slide in a presentation.
/// </summary>
internal sealed record PresentationSlide(string FilePath, int Line, string Text);

/// <summary>
/// Manages an interactive presentation session consisting of slides
/// that navigate the editor to specific files and lines with explanatory text.
/// </summary>
internal sealed class PresentationService
{
    private readonly List<PresentationSlide> _slides = [];
    private int _currentIndex = -1;

    /// <summary>Title of the active presentation, or null if no presentation is active.</summary>
    public string? Title { get; private set; }

    /// <summary>Whether a presentation is currently active.</summary>
    public bool IsActive => Title is not null;

    /// <summary>All slides in the current presentation.</summary>
    public IReadOnlyList<PresentationSlide> Slides => _slides;

    /// <summary>Zero-based index of the currently displayed slide, or -1 if none.</summary>
    public int CurrentIndex => _currentIndex;

    /// <summary>The currently displayed slide, or null.</summary>
    public PresentationSlide? CurrentSlide =>
        _currentIndex >= 0 && _currentIndex < _slides.Count ? _slides[_currentIndex] : null;

    /// <summary>Raised when the active slide changes (including on new/close).</summary>
    public event EventHandler? SlideChanged;

    /// <summary>
    /// Starts a new presentation, discarding any existing one.
    /// </summary>
    public void NewPresentation(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        Title = title;
        _slides.Clear();
        _currentIndex = -1;
        SlideChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Adds a slide to the end of the presentation.
    /// The current slide remains unchanged unless this is the first slide.
    /// </summary>
    public void AddSlide(string filePath, int line, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        PresentationSlide slide = new(filePath, Math.Max(0, line), text);
        _slides.Add(slide);

        if (_currentIndex < 0)
        {
            _currentIndex = 0;
        }

        SlideChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Moves to the next slide. Returns false if already at the last slide.
    /// </summary>
    public bool MoveNext()
    {
        if (_currentIndex >= _slides.Count - 1)
        {
            return false;
        }

        _currentIndex++;
        SlideChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Moves to the previous slide. Returns false if already at the first slide.
    /// </summary>
    public bool MoveBack()
    {
        if (_currentIndex <= 0)
        {
            return false;
        }

        _currentIndex--;
        SlideChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Navigates to a specific slide by index.
    /// </summary>
    public bool GoToSlide(int index)
    {
        if (index < 0 || index >= _slides.Count)
        {
            return false;
        }

        _currentIndex = index;
        SlideChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Closes the active presentation and clears all slides.
    /// </summary>
    public void Close()
    {
        Title = null;
        _slides.Clear();
        _currentIndex = -1;
        SlideChanged?.Invoke(this, EventArgs.Empty);
    }
}
