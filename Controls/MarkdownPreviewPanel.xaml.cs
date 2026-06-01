using KaneCode.Services;
using System.Windows;
using System.Windows.Controls;

namespace KaneCode.Controls;

/// <summary>
/// A read-only rich-text panel that renders markdown content into a styled FlowDocument.
/// Used as the preview pane for markdown (.md) files in the editor.
/// </summary>
public partial class MarkdownPreviewPanel : UserControl
{
    private string _markdownContent = string.Empty;

    public MarkdownPreviewPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Sets the raw markdown text to render. Call whenever the editor content changes
    /// or when switching to preview mode.
    /// </summary>
    public void SetMarkdownContent(string markdown)
    {
        _markdownContent = markdown ?? string.Empty;

        if (Visibility == Visibility.Visible)
        {
            RefreshPreview();
        }
    }

    /// <summary>
    /// Refreshes the rendered preview using the current markdown content.
    /// </summary>
    public void RefreshPreview()
    {
        MarkdownRenderer.Document = MarkdownRenderService.RenderToFlowDocument(_markdownContent);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is bool isVisible && isVisible)
        {
            RefreshPreview();
        }
    }
}
