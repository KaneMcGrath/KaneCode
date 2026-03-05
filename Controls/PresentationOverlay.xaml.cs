using KaneCode.Services.Ai;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

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
        RenderMarkdownInto(SlideTextBox, slide.Text);
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

    /// <summary>
    /// Parses markdown text and renders it into the given <see cref="RichTextBox"/>.
    /// Supports fenced code blocks, tables, inline code, and bold text.
    /// </summary>
    private void RenderMarkdownInto(RichTextBox richTextBox, string markdown)
    {
        FlowDocument document = new() { PagePadding = new Thickness(0) };

        string[] lines = markdown.Split('\n');
        bool inCodeBlock = false;
        List<string> codeLines = [];
        List<string> tableLines = [];

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushTable(document, tableLines);

                if (!inCodeBlock)
                {
                    inCodeBlock = true;
                    codeLines.Clear();
                }
                else
                {
                    document.Blocks.Add(CreateCodeBlock(string.Join('\n', codeLines)));
                    inCodeBlock = false;
                    codeLines.Clear();
                }

                continue;
            }

            if (inCodeBlock)
            {
                codeLines.Add(line);
                continue;
            }

            if (IsTableRow(line))
            {
                tableLines.Add(line);
                continue;
            }

            FlushTable(document, tableLines);
            document.Blocks.Add(CreateMarkdownParagraph(line));
        }

        FlushTable(document, tableLines);

        if (inCodeBlock && codeLines.Count > 0)
        {
            document.Blocks.Add(CreateCodeBlock(string.Join('\n', codeLines)));
        }

        richTextBox.Document = document;
    }

    private static bool IsTableRow(string line)
    {
        string trimmed = line.Trim();
        return trimmed.StartsWith('|') && trimmed.Contains('|', StringComparison.Ordinal) && trimmed.Length > 1;
    }

    private static bool IsTableSeparator(string line)
    {
        string trimmed = line.Trim().Trim('|');
        return trimmed.Length > 0 && trimmed.Replace("-", "", StringComparison.Ordinal)
            .Replace("|", "", StringComparison.Ordinal)
            .Replace(":", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal).Length == 0;
    }

    private static string[] SplitTableRow(string line)
    {
        string trimmed = line.Trim();

        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Split('|').Select(cell => cell.Trim()).ToArray();
    }

    private void FlushTable(FlowDocument document, List<string> tableLines)
    {
        if (tableLines.Count == 0)
        {
            return;
        }

        Brush borderBrush = FindBrush("AiChatBorder");
        Brush headerBackground = FindBrush("AiChatCodeBlockBackground");
        Brush foreground = FindBrush("PanelForeground");

        Table table = new()
        {
            CellSpacing = 0,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 4, 0, 4),
            Foreground = foreground
        };

        string[] firstRowCells = SplitTableRow(tableLines[0]);
        foreach (string _ in firstRowCells)
        {
            table.Columns.Add(new TableColumn());
        }

        TableRowGroup rowGroup = new();
        bool isFirstDataRow = true;

        foreach (string rowLine in tableLines)
        {
            if (IsTableSeparator(rowLine))
            {
                continue;
            }

            string[] cells = SplitTableRow(rowLine);
            TableRow tableRow = new();

            for (int i = 0; i < firstRowCells.Length; i++)
            {
                string cellText = i < cells.Length ? cells[i] : string.Empty;
                Paragraph paragraph = new() { Margin = new Thickness(0), Foreground = foreground };
                ParseInlineMarkdown(paragraph, cellText);

                TableCell cell = new(paragraph)
                {
                    Padding = new Thickness(6, 3, 6, 3),
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1)
                };

                if (isFirstDataRow)
                {
                    cell.Background = headerBackground;
                    paragraph.FontWeight = FontWeights.SemiBold;
                }

                tableRow.Cells.Add(cell);
            }

            rowGroup.Rows.Add(tableRow);
            isFirstDataRow = false;
        }

        table.RowGroups.Add(rowGroup);
        document.Blocks.Add(table);
        tableLines.Clear();
    }

    private Paragraph CreateCodeBlock(string code)
    {
        Paragraph paragraph = new()
        {
            Background = FindBrush("AiChatCodeBlockBackground"),
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 12,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 6, 0, 6),
            Foreground = FindBrush("AiChatCodeForeground")
        };

        paragraph.Inlines.Add(new Run(code));
        return paragraph;
    }

    private Paragraph CreateMarkdownParagraph(string line)
    {
        Paragraph paragraph = new()
        {
            Margin = new Thickness(0, 3, 0, 3),
            LineHeight = 20,
            Foreground = FindBrush("PanelForeground")
        };

        if (line.StartsWith("### ", StringComparison.Ordinal))
        {
            paragraph.FontSize = 14;
            paragraph.FontWeight = FontWeights.SemiBold;
            paragraph.Margin = new Thickness(0, 6, 0, 2);
            line = line[4..];
        }
        else if (line.StartsWith("## ", StringComparison.Ordinal))
        {
            paragraph.FontSize = 15;
            paragraph.FontWeight = FontWeights.Bold;
            paragraph.Margin = new Thickness(0, 8, 0, 2);
            line = line[3..];
        }
        else if (line.StartsWith("# ", StringComparison.Ordinal))
        {
            paragraph.FontSize = 16;
            paragraph.FontWeight = FontWeights.Bold;
            paragraph.Margin = new Thickness(0, 10, 0, 2);
            line = line[2..];
        }

        if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
        {
            paragraph.TextIndent = 0;
            paragraph.Margin = new Thickness(12, 1, 0, 1);
            line = "• " + line[2..];
        }

        ParseInlineMarkdown(paragraph, line);
        return paragraph;
    }

    private void ParseInlineMarkdown(Paragraph paragraph, string text)
    {
        string pattern = @"(\*\*(.+?)\*\*)|(`(.+?)`)";
        int lastIndex = 0;

        foreach (Match match in Regex.Matches(text, pattern))
        {
            if (match.Index > lastIndex)
            {
                paragraph.Inlines.Add(new Run(text[lastIndex..match.Index]));
            }

            if (match.Groups[2].Success)
            {
                paragraph.Inlines.Add(new Bold(new Run(match.Groups[2].Value)));
            }
            else if (match.Groups[4].Success)
            {
                Run codeRun = new(match.Groups[4].Value)
                {
                    FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                    Background = FindBrush("AiChatCodeBlockBackground"),
                    Foreground = FindBrush("AiChatCodeForeground"),
                    FontSize = 12
                };

                paragraph.Inlines.Add(codeRun);
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            paragraph.Inlines.Add(new Run(text[lastIndex..]));
        }
    }

    private Brush FindBrush(string key)
    {
        return TryFindResource(key) is Brush brush ? brush : Brushes.Gray;
    }
}
