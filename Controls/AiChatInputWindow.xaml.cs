using System;
using System.Windows;
using System.Windows.Input;

namespace KaneCode.Controls;

/// <summary>
/// A popup window providing an expanded text input area for composing AI chat prompts.
/// Enter key inserts a newline (does not send). The user must explicitly click Send.
/// </summary>
public partial class AiChatInputWindow : Window
{
    /// <summary>
    /// Raised when the user clicks the Send button, providing the final text content.
    /// </summary>
    public event Action<string>? SendRequested;

    /// <summary>
    /// Initializes the popup window with the given initial text.
    /// </summary>
    public AiChatInputWindow(string initialText, Window? owner)
    {
        InitializeComponent();
        InputBox.Text = initialText ?? string.Empty;
        InputBox.CaretIndex = InputBox.Text.Length;
        Owner = owner;

        Loaded += (_, _) => InputBox.Focus();
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        string text = InputBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        SendRequested?.Invoke(text);
        DialogResult = true;
        Close();
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // In the popup window, Enter always inserts a newline — never sends.
        // Shift+Enter also inserts a newline (default behavior for AcceptsReturn).
        if (e.Key == Key.Enter)
        {
            e.Handled = false; // Let the default AcceptsReturn handling insert the newline
            return;
        }
    }
}
