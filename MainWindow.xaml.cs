using KaneCode.Infrastructure;
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

        ApplyHotkeyBindings();
        HotkeyManager.BindingsChanged += ApplyHotkeyBindings;

        // Ctrl+Click triggers Go to Definition
        CodeEditor.PreviewMouseLeftButtonUp += CodeEditor_PreviewMouseLeftButtonUp;

        // Quick Info hover tooltips
        CodeEditor.TextArea.TextView.MouseHover += TextView_MouseHover;
        CodeEditor.TextArea.TextView.MouseHoverStopped += TextView_MouseHoverStopped;
        CodeEditor.TextArea.TextView.VisualLinesChanged += TextView_VisualLinesChanged;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        HotkeyManager.BindingsChanged -= ApplyHotkeyBindings;
        CodeEditor.TextArea.TextView.MouseHover -= TextView_MouseHover;
        CodeEditor.TextArea.TextView.MouseHoverStopped -= TextView_MouseHoverStopped;
        CodeEditor.TextArea.TextView.VisualLinesChanged -= TextView_VisualLinesChanged;
        CodeEditor.PreviewMouseLeftButtonUp -= CodeEditor_PreviewMouseLeftButtonUp;
        CloseQuickInfoPopup();
        _viewModel.Dispose();
    }

    /// <summary>
    /// Applies all hotkey bindings from HotkeyManager to window and editor input bindings.
    /// Called on startup and whenever bindings change.
    /// </summary>
    private void ApplyHotkeyBindings()
    {
        // Clear previous dynamic bindings
        InputBindings.Clear();
        CodeEditor.InputBindings.Clear();

        // Window-level bindings (menu commands)
        AddWindowBinding(HotkeyAction.NewFile, _viewModel.NewFileCommand);
        AddWindowBinding(HotkeyAction.OpenFile, _viewModel.OpenFileCommand);
        AddWindowBinding(HotkeyAction.OpenFolder, _viewModel.OpenFolderCommand);
        AddWindowBinding(HotkeyAction.OpenProject, _viewModel.OpenProjectCommand);
        AddWindowBinding(HotkeyAction.OpenSolution, _viewModel.OpenSolutionCommand);
        AddWindowBinding(HotkeyAction.Save, _viewModel.SaveCommand);
        AddWindowBinding(HotkeyAction.SaveAs, _viewModel.SaveAsCommand);
        AddWindowBinding(HotkeyAction.CloseTab, _viewModel.CloseTabCommand);
        AddWindowBinding(HotkeyAction.Undo, _viewModel.UndoCommand);
        AddWindowBinding(HotkeyAction.Redo, _viewModel.RedoCommand);
        AddWindowBinding(HotkeyAction.Cut, _viewModel.CutCommand);
        AddWindowBinding(HotkeyAction.Copy, _viewModel.CopyCommand);
        AddWindowBinding(HotkeyAction.Paste, _viewModel.PasteCommand);
        AddWindowBinding(HotkeyAction.OpenOptions, _viewModel.OpenOptionsCommand);
        AddWindowBinding(HotkeyAction.Exit, _viewModel.ExitCommand);

        // Editor-level bindings (need to go on the editor to intercept before AvalonEdit)
        AddEditorBinding(HotkeyAction.Find, _viewModel.FindCommand);
        AddEditorBinding(HotkeyAction.Replace, _viewModel.ReplaceCommand);
        AddEditorBinding(HotkeyAction.GoToDefinition,
            new RelayInputCommand(async () => await _viewModel.GoToDefinitionAsync()));
        AddEditorBinding(HotkeyAction.TriggerCompletion,
            new RelayInputCommand(async () => await _viewModel.ShowCompletionWindowAsync()));

        // Update menu gesture text displays
        UpdateMenuGestureText();
    }

    private void AddWindowBinding(HotkeyAction action, ICommand command)
    {
        var binding = HotkeyManager.Get(action);
        if (binding.Key == Key.None)
        {
            return;
        }

        InputBindings.Add(new KeyBinding(command, binding.Key, binding.Modifiers));
    }

    private void AddEditorBinding(HotkeyAction action, ICommand command)
    {
        var binding = HotkeyManager.Get(action);
        if (binding.Key == Key.None)
        {
            return;
        }

        CodeEditor.InputBindings.Add(new KeyBinding(command, binding.Key, binding.Modifiers));
    }

    /// <summary>
    /// Walks the menu tree and updates InputGestureText for items whose Header
    /// matches a known hotkey action.
    /// </summary>
    private void UpdateMenuGestureText()
    {
        var menuBar = (Menu)((DockPanel)Content).Children[0];
        foreach (var topItem in menuBar.Items.OfType<MenuItem>())
        {
            UpdateMenuItemGestures(topItem);
        }
    }

    private static readonly Dictionary<string, HotkeyAction> s_menuHeaderToAction = new(StringComparer.OrdinalIgnoreCase)
    {
        ["_New"] = HotkeyAction.NewFile,
        ["_Open File"] = HotkeyAction.OpenFile,
        ["Open _Folder"] = HotkeyAction.OpenFolder,
        ["Open _Project..."] = HotkeyAction.OpenProject,
        ["Open _Solution..."] = HotkeyAction.OpenSolution,
        ["_Save"] = HotkeyAction.Save,
        ["Save _As..."] = HotkeyAction.SaveAs,
        ["_Close Tab"] = HotkeyAction.CloseTab,
        ["_Undo"] = HotkeyAction.Undo,
        ["_Redo"] = HotkeyAction.Redo,
        ["Cu_t"] = HotkeyAction.Cut,
        ["_Copy"] = HotkeyAction.Copy,
        ["_Paste"] = HotkeyAction.Paste,
        ["Go to _Definition"] = HotkeyAction.GoToDefinition,
        ["_Options"] = HotkeyAction.OpenOptions,
        ["E_xit"] = HotkeyAction.Exit,
    };

    private static void UpdateMenuItemGestures(MenuItem menuItem)
    {
        if (menuItem.Header is string header && s_menuHeaderToAction.TryGetValue(header, out var action))
        {
            menuItem.InputGestureText = HotkeyManager.GetGestureText(action);
        }

        foreach (var child in menuItem.Items.OfType<MenuItem>())
        {
            UpdateMenuItemGestures(child);
        }
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