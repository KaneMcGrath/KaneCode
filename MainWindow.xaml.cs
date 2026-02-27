using KaneCode.Infrastructure;
using KaneCode.Models;
using KaneCode.Theming;
using KaneCode.ViewModels;
using ICSharpCode.AvalonEdit.Rendering;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Layout;

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

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.CodeActionsReady += OnCodeActionsReady;

        // Ctrl+Click triggers Go to Definition
        CodeEditor.PreviewMouseLeftButtonUp += CodeEditor_PreviewMouseLeftButtonUp;

        // Quick Info hover tooltips
        CodeEditor.TextArea.TextView.MouseHover += TextView_MouseHover;
        CodeEditor.TextArea.TextView.MouseHoverStopped += TextView_MouseHoverStopped;
        CodeEditor.TextArea.TextView.VisualLinesChanged += TextView_VisualLinesChanged;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.CodeActionsReady -= OnCodeActionsReady;
        HotkeyManager.BindingsChanged -= ApplyHotkeyBindings;
        CodeEditor.TextArea.TextView.MouseHover -= TextView_MouseHover;
        CodeEditor.TextArea.TextView.MouseHoverStopped -= TextView_MouseHoverStopped;
        CodeEditor.TextArea.TextView.VisualLinesChanged -= TextView_VisualLinesChanged;
        CodeEditor.PreviewMouseLeftButtonUp -= CodeEditor_PreviewMouseLeftButtonUp;
        CloseQuickInfoPopup();
        _viewModel.Dispose();
    }

    /// <summary>
    /// Brings the Find References panel to the front whenever a search is triggered.
    /// FindReferencesStatusText is set synchronously at the start of every search,
    /// so this fires for both the hotkey path and the menu-binding path.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.FindReferencesStatusText))
        {
            DockManager.ActiveContent = FindReferencesPanel;
        }
    }

    private void ViewMenuPanel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
        {
            return;
        }

        if (menuItem.Tag is not LayoutAnchorable anchorable)
        {
            return;
        }

        ShowLayoutAnchorable(anchorable);
    }

    private void ShowLayoutAnchorable(LayoutAnchorable anchorable)
    {
        if (!anchorable.IsVisible)
        {
            anchorable.Show();
        }

        anchorable.IsActive = true;
        anchorable.IsSelected = true;
        DockManager.ActiveContent = anchorable.Content ?? anchorable;
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
        AddWindowBinding(HotkeyAction.BuildProject, _viewModel.BuildCommand);
        AddWindowBinding(HotkeyAction.RunProject, _viewModel.RunCommand);
        AddWindowBinding(HotkeyAction.CancelBuild, _viewModel.CancelBuildCommand);

        // Editor-level bindings (need to go on the editor to intercept before AvalonEdit)
        AddEditorBinding(HotkeyAction.Find, _viewModel.FindCommand);
        AddEditorBinding(HotkeyAction.Replace, _viewModel.ReplaceCommand);
        AddEditorBinding(HotkeyAction.GoToDefinition,
            new RelayInputCommand(async () => await _viewModel.GoToDefinitionAsync()));
        AddEditorBinding(HotkeyAction.FindReferences,
            new RelayInputCommand(async () => await _viewModel.FindReferencesAsync()));
        AddEditorBinding(HotkeyAction.TriggerCompletion,
            new RelayInputCommand(async () => await _viewModel.ShowCompletionWindowAsync()));
        AddEditorBinding(HotkeyAction.CodeActions,
            new RelayInputCommand(async () => await _viewModel.ShowCodeActionsAsync()));
        AddEditorBinding(HotkeyAction.Rename,
            new RelayInputCommand(async () => await _viewModel.RenameSymbolAsync()));
        AddEditorBinding(HotkeyAction.ExtractMethod,
            new RelayInputCommand(async () => await _viewModel.ExtractMethodAsync()));

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
        ["Find _References"] = HotkeyAction.FindReferences,
        ["Code _Actions"] = HotkeyAction.CodeActions,
        ["_Rename"] = HotkeyAction.Rename,
        ["_Extract Method"] = HotkeyAction.ExtractMethod,
        ["_Options"] = HotkeyAction.OpenOptions,
        ["E_xit"] = HotkeyAction.Exit,
        ["_Build Project"] = HotkeyAction.BuildProject,
        ["_Run Project"] = HotkeyAction.RunProject,
        ["_Cancel"] = HotkeyAction.CancelBuild,
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

    private void FindReferencesPanel_NavigateRequested(object? sender, ReferenceItem item)
    {
        _viewModel.NavigateToReference(item);
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

    private void OnCodeActionsReady(IReadOnlyList<Models.CodeActionItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        // Position the popup near the caret
        var caretPos = CodeEditor.TextArea.TextView.GetVisualPosition(
            CodeEditor.TextArea.Caret.Position,
            ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineBottom);
        var screenPoint = CodeEditor.TextArea.TextView.PointToScreen(caretPos);
        var windowPoint = PointFromScreen(screenPoint);

        CodeActionPopup.Show(items, this, windowPoint.X, windowPoint.Y);
    }

    private async void CodeActionLightBulb_ActionSelected(object? sender, Models.CodeActionItem item)
    {
        await _viewModel.ApplyCodeActionAsync(item);
        CodeEditor.TextArea.Focus();
    }

    private void CloseQuickInfoPopup()
    {
        if (_quickInfoPopup is not null)
        {
            _quickInfoPopup.IsOpen = false;
            _quickInfoPopup = null;
        }
    }

    // ── Editor context menu ────────────────────────────────────────────

    private void EditorContextMenu_Cut(object sender, RoutedEventArgs e) => CodeEditor.Cut();
    private void EditorContextMenu_Copy(object sender, RoutedEventArgs e) => CodeEditor.Copy();
    private void EditorContextMenu_Paste(object sender, RoutedEventArgs e) => CodeEditor.Paste();
    private void EditorContextMenu_Undo(object sender, RoutedEventArgs e) => CodeEditor.Undo();
    private void EditorContextMenu_Redo(object sender, RoutedEventArgs e) => CodeEditor.Redo();

    private async void EditorContextMenu_GoToDefinition(object sender, RoutedEventArgs e)
    {
        await _viewModel.GoToDefinitionAsync();
    }

    private async void EditorContextMenu_FindReferences(object sender, RoutedEventArgs e)
    {
        await _viewModel.FindReferencesAsync();
    }

    private async void EditorContextMenu_CodeActions(object sender, RoutedEventArgs e)
    {
        await _viewModel.ShowCodeActionsAsync();
    }

    private async void EditorContextMenu_Rename(object sender, RoutedEventArgs e)
    {
        await _viewModel.RenameSymbolAsync();
    }

    private async void EditorContextMenu_ExtractMethod(object sender, RoutedEventArgs e)
    {
        await _viewModel.ExtractMethodAsync();
    }

    // ── Explorer context menu ──────────────────────────────────────────

    private void ExplorerContextMenu_Open(object sender, RoutedEventArgs e)
    {
        if (FileTree.SelectedItem is ProjectItem item)
        {
            _viewModel.OnProjectItemSelected(item);
        }
    }

    private void ExplorerContextMenu_NewFile_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem templateMenu)
        {
            return;
        }

        templateMenu.Items.Clear();

        IReadOnlyList<FileTemplate> templates;
        try
        {
            templates = _viewModel.GetExplorerFileTemplates();
        }
        catch (IOException ex)
        {
            MessageBox.Show($"Could not load templates:\n{ex.Message}", "Template Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        catch (JsonException ex)
        {
            MessageBox.Show($"Template file is invalid JSON:\n{ex.Message}", "Template Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        foreach (var template in templates)
        {
            var item = new MenuItem
            {
                Header = template.Name,
                Tag = template.Name
            };

            item.Click += ExplorerContextMenu_NewFileFromTemplate;
            templateMenu.Items.Add(item);
        }

        if (templateMenu.Items.Count == 0)
        {
            templateMenu.Items.Add(new MenuItem
            {
                Header = "(No templates)",
                IsEnabled = false
            });
        }
    }

    private void ExplorerContextMenu_NewFileFromTemplate(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string templateName })
        {
            return;
        }

        _viewModel.CreateFileFromTemplate(templateName, FileTree.SelectedItem as ProjectItem);
    }

    private void ExplorerContextMenu_CopyPath(object sender, RoutedEventArgs e)
    {
        if (FileTree.SelectedItem is ProjectItem item)
        {
            Clipboard.SetText(item.FullPath);
        }
    }

    private void ExplorerContextMenu_OpenInFileExplorer(object sender, RoutedEventArgs e)
    {
        if (FileTree.SelectedItem is not ProjectItem item)
        {
            return;
        }

        var path = item.IsDirectory ? item.FullPath : Path.GetDirectoryName(item.FullPath);
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
    }

    // ── Tab strip context menu ─────────────────────────────────────────

    private void TabContextMenu_Close(object sender, RoutedEventArgs e)
    {
        if (GetTabFromContextMenu(sender) is { } tab)
        {
            _viewModel.CloseTabCommand.Execute(tab);
        }
    }

    private void TabContextMenu_CloseOthers(object sender, RoutedEventArgs e)
    {
        if (GetTabFromContextMenu(sender) is not { } keepTab)
        {
            return;
        }

        foreach (var tab in _viewModel.OpenTabs.Where(t => t != keepTab).ToList())
        {
            _viewModel.CloseTabCommand.Execute(tab);
        }
    }

    private void TabContextMenu_CloseAll(object sender, RoutedEventArgs e)
    {
        foreach (var tab in _viewModel.OpenTabs.ToList())
        {
            _viewModel.CloseTabCommand.Execute(tab);
        }
    }

    private void TabContextMenu_CopyPath(object sender, RoutedEventArgs e)
    {
        if (GetTabFromContextMenu(sender) is { } tab && !string.IsNullOrEmpty(tab.FilePath))
        {
            Clipboard.SetText(tab.FilePath);
        }
    }

    private static OpenFileTab? GetTabFromContextMenu(object sender)
    {
        if (sender is MenuItem menuItem
            && menuItem.Parent is ContextMenu contextMenu
            && contextMenu.PlacementTarget is FrameworkElement fe)
        {
            return fe.DataContext as OpenFileTab;
        }

        return null;
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