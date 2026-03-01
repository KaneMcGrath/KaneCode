using KaneCode.Infrastructure;
using KaneCode.Models;
using KaneCode.Services;
using KaneCode.Theming;
using KaneCode.ViewModels;
using ICSharpCode.AvalonEdit.Rendering;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.Win32;
using System.Collections.Generic;
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
    private readonly TemplateEngineService _templateEngine = new();
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
        _templateEngine.Dispose();
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

    private async void FileMenu_NewProject_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<ITemplateInfo> templates;
        try
        {
            templates = await _templateEngine.GetProjectTemplatesAsync();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show($"Could not discover SDK templates:\n{ex.Message}", "New Project",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var dialogState = ShowNewProjectDialog(templates);
        if (dialogState is null)
        {
            return;
        }

        try
        {
            var projectDir = Path.Combine(dialogState.DestinationDirectory, dialogState.ProjectName);

            if (dialogState.CreateSolution)
            {
                await _templateEngine.CreateProjectAsync(
                    dialogState.Template,
                    dialogState.ProjectName,
                    projectDir,
                    dialogState.TargetFramework);

                var solutionPath = await _templateEngine.CreateSolutionAsync(
                    dialogState.ProjectName,
                    projectDir);

                await _viewModel.OpenSolutionByPathAsync(solutionPath);
                OpenFirstSourceFile(projectDir);
            }
            else
            {
                await _templateEngine.CreateProjectAsync(
                    dialogState.Template,
                    dialogState.ProjectName,
                    projectDir,
                    dialogState.TargetFramework);

                var csprojPath = Directory.EnumerateFiles(projectDir, "*.csproj").FirstOrDefault();
                if (!string.IsNullOrEmpty(csprojPath))
                {
                    await _viewModel.OpenProjectByPathAsync(csprojPath);
                }

                OpenFirstSourceFile(projectDir);
            }
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "New Project", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (IOException ex)
        {
            MessageBox.Show($"Could not create project:\n{ex.Message}", "New Project",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private NewProjectDialogState? ShowNewProjectDialog(IReadOnlyList<ITemplateInfo> templates)
    {
        if (templates.Count == 0)
        {
            MessageBox.Show("No project templates are available.\nEnsure the .NET SDK is installed.", "New Project",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var dialog = new Window
        {
            Title = "New Project",
            Width = 520,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = this
        };

        var rootPanel = new Grid { Margin = new Thickness(12) };
        rootPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        rootPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rootPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        AddLabel(rootPanel, "Name:", 0);
        var nameTextBox = new TextBox { Text = "MyProject", Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(nameTextBox, 0);
        Grid.SetColumn(nameTextBox, 1);
        Grid.SetColumnSpan(nameTextBox, 2);
        rootPanel.Children.Add(nameTextBox);

        AddLabel(rootPanel, "Template:", 1);
        var displayItems = templates.Select(t => new TemplateDisplayItem(t)).ToList();
        var templateCombo = new ComboBox
        {
            ItemsSource = displayItems,
            SelectedIndex = 0,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(templateCombo, 1);
        Grid.SetColumn(templateCombo, 1);
        Grid.SetColumnSpan(templateCombo, 2);
        rootPanel.Children.Add(templateCombo);

        var frameworkLabel = new TextBlock
        {
            Text = "Framework:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 8)
        };
        Grid.SetRow(frameworkLabel, 2);
        Grid.SetColumn(frameworkLabel, 0);
        rootPanel.Children.Add(frameworkLabel);

        var frameworkCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(frameworkCombo, 2);
        Grid.SetColumn(frameworkCombo, 1);
        Grid.SetColumnSpan(frameworkCombo, 2);
        rootPanel.Children.Add(frameworkCombo);

        void RefreshFrameworks()
        {
            if (templateCombo.SelectedItem is not TemplateDisplayItem item)
            {
                return;
            }

            var choices = TemplateEngineService.GetFrameworkChoices(item.Info);
            frameworkCombo.ItemsSource = choices.Count > 0 ? (IEnumerable<object>)choices : [new FrameworkChoice("(Default)", null)];
            frameworkCombo.SelectedIndex = 0;
            frameworkCombo.IsEnabled = choices.Count > 0;
        }

        templateCombo.SelectionChanged += (_, _) => RefreshFrameworks();
        RefreshFrameworks();

        AddLabel(rootPanel, "Location:", 3);
        var destinationTextBox = new TextBox
        {
            Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Margin = new Thickness(0, 0, 8, 8)
        };
        Grid.SetRow(destinationTextBox, 3);
        Grid.SetColumn(destinationTextBox, 1);
        rootPanel.Children.Add(destinationTextBox);

        var browseButton = new Button
        {
            Content = "Browse...",
            Width = 90,
            Margin = new Thickness(0, 0, 0, 8)
        };
        browseButton.Click += (_, _) =>
        {
            var folderDialog = new OpenFolderDialog
            {
                Title = "Select Project Location",
                InitialDirectory = destinationTextBox.Text
            };

            if (folderDialog.ShowDialog() == true)
            {
                destinationTextBox.Text = folderDialog.FolderName;
            }
        };
        Grid.SetRow(browseButton, 3);
        Grid.SetColumn(browseButton, 2);
        rootPanel.Children.Add(browseButton);

        var createSolutionCheckBox = new CheckBox
        {
            Content = "Create solution (.sln)",
            IsChecked = true,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(createSolutionCheckBox, 4);
        Grid.SetColumn(createSolutionCheckBox, 1);
        Grid.SetColumnSpan(createSolutionCheckBox, 2);
        rootPanel.Children.Add(createSolutionCheckBox);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        NewProjectDialogState? result = null;
        var createButton = new Button
        {
            Content = "Create",
            Width = 90,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        createButton.Click += (_, _) =>
        {
            var name = nameTextBox.Text.Trim();
            var location = destinationTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Project name is required.", "New Project",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(location))
            {
                MessageBox.Show("Project location is required.", "New Project",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!Directory.Exists(location))
            {
                MessageBox.Show("Project location does not exist.", "New Project",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (templateCombo.SelectedItem is not TemplateDisplayItem selectedItem)
            {
                MessageBox.Show("Select a template.", "New Project",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            result = new NewProjectDialogState(
                name,
                selectedItem.Info,
                location,
                frameworkCombo.IsEnabled && frameworkCombo.SelectedItem is FrameworkChoice fc ? fc.Moniker : null,
                createSolutionCheckBox.IsChecked == true);
            dialog.DialogResult = true;
        };
        buttonPanel.Children.Add(createButton);

        var cancelButton = new Button
        {
            Content = "Cancel",
            Width = 90,
            IsCancel = true
        };
        buttonPanel.Children.Add(cancelButton);

        Grid.SetRow(buttonPanel, 6);
        Grid.SetColumn(buttonPanel, 1);
        Grid.SetColumnSpan(buttonPanel, 2);
        rootPanel.Children.Add(buttonPanel);

        dialog.Content = rootPanel;
        nameTextBox.Loaded += (_, _) =>
        {
            nameTextBox.Focus();
            nameTextBox.SelectAll();
        };

        return dialog.ShowDialog() == true ? result : null;
    }

    private static void AddLabel(Grid rootPanel, string text, int row)
    {
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 8)
        };

        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        rootPanel.Children.Add(label);
    }

    /// <summary>
    /// Opens the first <c>.cs</c> source file found in the given directory
    /// so the user lands in the editor immediately after project creation.
    /// </summary>
    private void OpenFirstSourceFile(string projectDirectory)
    {
        if (!Directory.Exists(projectDirectory))
        {
            return;
        }

        var firstSource = Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(firstSource))
        {
            _viewModel.OpenFileByPath(firstSource);
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

        // Project/Solution nodes point to a file; resolve to directory
        var path = item.ItemType switch
        {
            ProjectItemType.Project or ProjectItemType.Solution => Path.GetDirectoryName(item.FullPath),
            _ when item.IsDirectory => item.FullPath,
            _ => Path.GetDirectoryName(item.FullPath)
        };

        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
    }

    private void ExplorerContextMenu_Delete(object sender, RoutedEventArgs e)
    {
        _viewModel.DeleteExplorerItem(FileTree.SelectedItem as ProjectItem);
    }

    private void ExplorerContextMenu_Rename(object sender, RoutedEventArgs e)
    {
        _viewModel.RenameExplorerItem(FileTree.SelectedItem as ProjectItem);
    }

    private void ExplorerContextMenu_NewFolder(object sender, RoutedEventArgs e)
    {
        _viewModel.CreateNewFolder(FileTree.SelectedItem as ProjectItem);
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

    private sealed record NewProjectDialogState(
        string ProjectName,
        ITemplateInfo Template,
        string DestinationDirectory,
        string? TargetFramework,
        bool CreateSolution);

    /// <summary>
    /// Wraps <see cref="ITemplateInfo"/> for display in a combo box.
    /// </summary>
    private sealed record TemplateDisplayItem(ITemplateInfo Info)
    {
        public override string ToString()
        {
            var shortName = Info.ShortNameList.Count > 0 ? Info.ShortNameList[0] : "";
            return $"{Info.Name} ({shortName})";
        }
    }
}