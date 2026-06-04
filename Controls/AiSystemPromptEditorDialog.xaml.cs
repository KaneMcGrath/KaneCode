using System.Windows;

namespace KaneCode.Controls;

/// <summary>
/// Dialog for editing the per-conversation system prompt.
/// </summary>
public partial class AiSystemPromptEditorDialog : Window
{
    /// <summary>
    /// The edited system prompt, or null if the user cancelled.
    /// </summary>
    public string? EditedPrompt { get; private set; }

    public AiSystemPromptEditorDialog(string? currentPrompt, Window owner)
    {
        InitializeComponent();
        Owner = owner;
        PromptTextBox.Text = currentPrompt ?? string.Empty;
        PromptTextBox.Focus();
        PromptTextBox.SelectAll();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string trimmed = PromptTextBox.Text.Trim();
        EditedPrompt = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
