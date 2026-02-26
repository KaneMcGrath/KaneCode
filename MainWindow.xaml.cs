using KaneCode.Models;
using KaneCode.Theming;
using KaneCode.ViewModels;
using ICSharpCode.AvalonEdit.Rendering;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace KaneCode;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private Popup? _quickInfoPopup;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.AttachEditor(CodeEditor);

        // Ctrl+Space triggers code completion
        CodeEditor.InputBindings.Add(new KeyBinding(
            new RelayInputCommand(async () => await _viewModel.ShowCompletionWindowAsync()),
            Key.Space,
            ModifierKeys.Control));

        CodeEditor.InputBindings.Add(new KeyBinding(
            _viewModel.FindCommand,
            Key.F,
            ModifierKeys.Control));

        CodeEditor.InputBindings.Add(new KeyBinding(
            _viewModel.ReplaceCommand,
            Key.H,
            ModifierKeys.Control));

        // F12 triggers Go to Definition
        CodeEditor.InputBindings.Add(new KeyBinding(
            new RelayInputCommand(async () => await _viewModel.GoToDefinitionAsync()),
            Key.F12,
            ModifierKeys.None));

        // Ctrl+Click triggers Go to Definition
        CodeEditor.PreviewMouseLeftButtonUp += CodeEditor_PreviewMouseLeftButtonUp;

        // Quick Info hover tooltips
        CodeEditor.TextArea.TextView.MouseHover += TextView_MouseHover;
        CodeEditor.TextArea.TextView.MouseHoverStopped += TextView_MouseHoverStopped;
        CodeEditor.TextArea.TextView.VisualLinesChanged += TextView_VisualLinesChanged;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CodeEditor.TextArea.TextView.MouseHover -= TextView_MouseHover;
        CodeEditor.TextArea.TextView.MouseHoverStopped -= TextView_MouseHoverStopped;
        CodeEditor.TextArea.TextView.VisualLinesChanged -= TextView_VisualLinesChanged;
        CodeEditor.PreviewMouseLeftButtonUp -= CodeEditor_PreviewMouseLeftButtonUp;
        CloseQuickInfoPopup();
        _viewModel.Dispose();
    }

    private async void CodeEditor_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        await _viewModel.GoToDefinitionAsync();
        e.Handled = true;
    }

    private void FileTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeView tree && tree.SelectedItem is ProjectItem item)
        {
            _viewModel.OnProjectItemSelected(item);
            e.Handled = true;
        }
    }

    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is OpenFileTab tab)
        {
            _viewModel.SwitchToTab(tab);
        }
    }

    private void ErrorList_NavigateRequested(object? sender, DiagnosticItem item)
    {
        _viewModel.NavigateToDiagnostic(item);
    }

    private async void TextView_MouseHover(object? sender, MouseEventArgs e)
    {
        var textView = CodeEditor.TextArea.TextView;
        var position = textView.GetPositionFloor(e.GetPosition(textView) + textView.ScrollOffset);
        if (position is null)
        {
            return;
        }

        var offset = CodeEditor.Document.GetOffset(position.Value.Location);
        var result = await _viewModel.GetQuickInfoAsync(offset);
        if (result is null)
        {
            return;
        }

        ShowQuickInfoPopup(result.Text, e.GetPosition(this));
    }

    private void TextView_MouseHoverStopped(object? sender, MouseEventArgs e)
    {
        CloseQuickInfoPopup();
    }

    private void TextView_VisualLinesChanged(object? sender, EventArgs e)
    {
        CloseQuickInfoPopup();
    }

    private void ShowQuickInfoPopup(string text, Point position)
    {
        CloseQuickInfoPopup();

        var border = new Border
        {
            Padding = new Thickness(8, 4, 8, 4),
            MaxWidth = 600,
            CornerRadius = new CornerRadius(2)
        };

        if (Application.Current.TryFindResource(ThemeResourceKeys.TooltipBackground) is Brush bgBrush)
        {
            border.Background = bgBrush;
        }
        else
        {
            border.Background = SystemColors.InfoBrush;
        }

        if (Application.Current.TryFindResource(ThemeResourceKeys.TooltipBorder) is Brush borderBrush)
        {
            border.BorderBrush = borderBrush;
            border.BorderThickness = new Thickness(1);
        }

        var textBlock = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 12
        };

        if (Application.Current.TryFindResource(ThemeResourceKeys.TooltipForeground) is Brush fgBrush)
        {
            textBlock.Foreground = fgBrush;
        }

        border.Child = textBlock;

        _quickInfoPopup = new Popup
        {
            Child = border,
            Placement = PlacementMode.Relative,
            PlacementTarget = this,
            HorizontalOffset = position.X,
            VerticalOffset = position.Y + 16,
            AllowsTransparency = true,
            IsOpen = true
        };
    }

    private void CloseQuickInfoPopup()
    {
        if (_quickInfoPopup is not null)
        {
            _quickInfoPopup.IsOpen = false;
            _quickInfoPopup = null;
        }
    }

    /// <summary>
    /// Simple ICommand wrapper for async actions used by input bindings.
    /// </summary>
    private sealed class RelayInputCommand : ICommand
    {
        private readonly Func<Task> _execute;

        public RelayInputCommand(Func<Task> execute)
        {
            _execute = execute;
        }

        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public async void Execute(object? parameter) => await _execute();
    }
}