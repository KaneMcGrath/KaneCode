using KaneCode.Infrastructure;
using KaneCode.Models;
using KaneCode.Services;
using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Modes;
using KaneCode.Services.Ai.Tools;
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
using System.Windows.Documents;
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
    private readonly ThemeManager _themeManager = new();

    /// <summary>Exposes the theme manager for child controls that open the options dialog.</summary>
    internal ThemeManager ThemeManagerInstance => _themeManager;

    private readonly TemplateEngineService _templateEngine = new();
    private readonly AiProviderRegistry _aiProviderRegistry = new();
    private readonly AgentToolRegistry _agentToolRegistry = new();
    private readonly AiChatModeRegistry _aiChatModeRegistry = new();
    private readonly AiDebugLogService _aiDebugLogService = new();
    private readonly ExternalContextDirectoryRegistry _externalContextDirectoryRegistry = new();
    private readonly PresentationService _presentationService = new();
    private readonly PresentationLineHighlightRenderer _presentationLineHighlightRenderer = new();
    private readonly NuGetService _nuGetService = new();
    private Popup? _quickInfoPopup;
    private bool _isQuickInfoPinned;
    private Popup? _renamePreviewPopup;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // Fix maximize covering the taskbar when using custom WindowChrome
        WindowMaximizeHelper.Attach(this);

        // Wire up theme selector
        ThemeSelector.ItemsSource = _themeManager.AvailableThemes;
        ThemeSelector.SelectedItem = _themeManager.CurrentTheme;

        // Apply the initial AvalonDock theme
        DockManager.Theme = _themeManager.CurrentTheme.AvalonDockTheme;

        // Notify ViewModel of theme changes so it can refresh the editor
        _themeManager.ThemeChanged += _viewModel.OnThemeChanged;
        _viewModel.ThemeManager = _themeManager;

        Loaded += OnLoaded;
        Closed += OnClosed;
        RegisterAiChatModes();
        RegisterAgentTools();
        WirePresentationOverlay();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.AttachEditor(CodeEditor);
        CodeEditor.TextArea.TextView.BackgroundRenderers.Add(_presentationLineHighlightRenderer);

        ApplyHotkeyBindings();
        HotkeyManager.BindingsChanged += ApplyHotkeyBindings;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.CodeActionsReady += OnCodeActionsReady;
        _viewModel.InlineRenameRequested += OnInlineRenameRequested;
        _viewModel.NuGetPackageHighlightRequested += OnNuGetPackageHighlightRequested;

        // Ctrl+Click triggers Go to Definition
        CodeEditor.PreviewMouseLeftButtonUp += CodeEditor_PreviewMouseLeftButtonUp;

        // Quick Info hover tooltips
        CodeEditor.TextArea.TextView.MouseHover += TextView_MouseHover;
        CodeEditor.TextArea.TextView.MouseHoverStopped += TextView_MouseHoverStopped;
        CodeEditor.TextArea.TextView.VisualLinesChanged += TextView_VisualLinesChanged;

        // Markdown preview updates when editor text changes
        CodeEditor.TextChanged += OnCodeEditorTextChanged;

        // Git Changes panel operations (wired here because EventHandler<T> can't be attributed in XAML)
        GitChangesPanel.StageRequested    += GitChangesPanel_StageRequested;
        GitChangesPanel.StageAllRequested += GitChangesPanel_StageAllRequested;
        GitChangesPanel.UnstageRequested    += GitChangesPanel_UnstageRequested;
        GitChangesPanel.UnstageAllRequested += GitChangesPanel_UnstageAllRequested;
        GitChangesPanel.DiscardRequested  += GitChangesPanel_DiscardRequested;
        GitChangesPanel.DiffRequested += GitChangesPanel_DiffRequested;
        GitChangesPanel.AcceptCurrentConflictRequested += GitChangesPanel_AcceptCurrentConflictRequested;
        GitChangesPanel.AcceptIncomingConflictRequested += GitChangesPanel_AcceptIncomingConflictRequested;
        GitChangesPanel.AcceptBothConflictRequested += GitChangesPanel_AcceptBothConflictRequested;

        // Initialize AI provider registry and configure the chat panel
        _aiProviderRegistry.Reload();
        ConfigureAiChatPanel();

        // Process command-line arguments for opening files/projects/folders
        ProcessCommandLineArgs();
    }

    /// <summary>
    /// Processes command-line arguments passed to the application.
    /// Supports opening files, projects (.csproj), solutions (.sln, .slnx), and folders.
    /// When multiple paths are provided, the first project/solution/folder is loaded first,
    /// then individual files are opened within it.
    /// </summary>
    private void ProcessCommandLineArgs()
    {
        string[] args = App.CommandLineArgs;

        if (args.Length == 0)
        {
            return;
        }

        // Separate arguments into categories
        var projectPaths = new List<string>();
        var folderPaths = new List<string>();
        var filePaths = new List<string>();

        foreach (string arg in args)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(arg);
            }
            catch (Exception)
            {
                // Skip invalid paths
                continue;
            }

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                // Path doesn't exist — skip it
                continue;
            }

            string ext = Path.GetExtension(fullPath);
            bool isSolution = ext.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase);

            if (isSolution || ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                projectPaths.Add(fullPath);
            }
            else if (Directory.Exists(fullPath))
            {
                folderPaths.Add(fullPath);
            }
            else
            {
                filePaths.Add(fullPath);
            }
        }

        // Load the first project/solution if provided
        Task? loadTask = null;

        if (projectPaths.Count > 0)
        {
            string firstProject = projectPaths[0];
            string ext = Path.GetExtension(firstProject);
            bool isSolution = ext.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase);

            if (isSolution)
            {
                loadTask = _viewModel.OpenSolutionByPathAsync(firstProject);
            }
            else
            {
                loadTask = _viewModel.OpenProjectByPathAsync(firstProject);
            }
        }
        else if (folderPaths.Count > 0)
        {
            _viewModel.LoadProjectRoot(folderPaths[0]);
        }

        // Open individual file paths. If a project/solution was loaded, we need to
        // wait for it to finish before opening files so they land in the correct context.
        if (filePaths.Count > 0)
        {
            if (loadTask is not null)
            {
                // Chain file opens after the project load completes
                _ = loadTask.ContinueWith(_ =>
                {
                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        foreach (string filePath in filePaths)
                        {
                            _viewModel.OpenFileByPath(filePath);
                        }
                    });
                }, TaskContinuationOptions.ExecuteSynchronously);
            }
            else
            {
                foreach (string filePath in filePaths)
                {
                    _viewModel.OpenFileByPath(filePath);
                }
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.CodeActionsReady -= OnCodeActionsReady;
        _viewModel.InlineRenameRequested -= OnInlineRenameRequested;
        _viewModel.NuGetPackageHighlightRequested -= OnNuGetPackageHighlightRequested;
        CloseRenamePreviewPopup();
        HotkeyManager.BindingsChanged -= ApplyHotkeyBindings;
        _themeManager.ThemeChanged -= _viewModel.OnThemeChanged;
        CodeEditor.TextArea.TextView.MouseHover -= TextView_MouseHover;
        CodeEditor.TextArea.TextView.MouseHoverStopped -= TextView_MouseHoverStopped;
        CodeEditor.TextArea.TextView.VisualLinesChanged -= TextView_VisualLinesChanged;
        CodeEditor.PreviewMouseLeftButtonUp -= CodeEditor_PreviewMouseLeftButtonUp;
        CodeEditor.TextChanged -= OnCodeEditorTextChanged;
        GitChangesPanel.StageRequested    -= GitChangesPanel_StageRequested;
        GitChangesPanel.StageAllRequested -= GitChangesPanel_StageAllRequested;
        GitChangesPanel.UnstageRequested    -= GitChangesPanel_UnstageRequested;
        GitChangesPanel.UnstageAllRequested -= GitChangesPanel_UnstageAllRequested;
        GitChangesPanel.DiscardRequested  -= GitChangesPanel_DiscardRequested;
        GitChangesPanel.DiffRequested -= GitChangesPanel_DiffRequested;
        GitChangesPanel.AcceptCurrentConflictRequested -= GitChangesPanel_AcceptCurrentConflictRequested;
        GitChangesPanel.AcceptIncomingConflictRequested -= GitChangesPanel_AcceptIncomingConflictRequested;
        GitChangesPanel.AcceptBothConflictRequested -= GitChangesPanel_AcceptBothConflictRequested;
        CloseQuickInfoPopup();
        _viewModel.Dispose();
        _templateEngine.Dispose();
        _aiProviderRegistry.Dispose();
        _nuGetService.Dispose();
    }

    /// <summary>
    /// Brings the Find References panel to the front whenever a search is triggered.
    /// FindReferencesStatusText is set synchronously at the start of every search,
    /// so this fires for both the hotkey path and the menu-binding path.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveTab))
        {
            // Sync the TabControl's visual selection when ActiveTab changes
            // programmatically (e.g., via OpenFileByPath or SwitchToTab).
            // This is needed because we removed the SelectedItem two-way binding
            // to fix a circular-activation bug (the binding set ActiveTab before
            // SelectionChanged fired, causing SwitchToTab to short-circuit).
            EditorTabControl.SelectedItem = _viewModel.ActiveTab;
            UpdatePresentationLineHighlight();
            UpdateMarkdownToolbar();
        }

        if (e.PropertyName == nameof(MainViewModel.BuildSummary)
            && _viewModel.BuildSummary is string summary
            && (summary.StartsWith("Building") || summary.StartsWith("Running")
                || summary.StartsWith("Build") || summary.StartsWith("Run ")))
        {
            ShowLayoutAnchorable(BuildOutputAnchorable);
        }

        if (e.PropertyName == nameof(MainViewModel.FindReferencesStatusText))
        {
            DockManager.ActiveContent = FindReferencesPanel;
        }

        if (e.PropertyName == nameof(MainViewModel.ProjectRootPath)
            && !string.IsNullOrWhiteSpace(_viewModel.ProjectRootPath))
        {
            // A project, solution, or folder was loaded — switch from Application mode to Agent mode
            // so the AI can inspect files, gather diagnostics, and make edits.
            AiChatPanel.SwitchToMode("agent");
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

    private void EditMenu_NuGetPackageManager_Click(object? sender, RoutedEventArgs e)
    {
        // Collect project paths from the loaded solution/project
        var projectPaths = NuGetService.GetProjectPaths(
            _viewModel.LoadedSolutionProjectPaths,
            _viewModel.LoadedProjectOrSolutionPath);

        if (projectPaths.Count == 0)
        {
            MessageBox.Show("Open a .NET project or solution first to manage NuGet packages.",
                "NuGet Package Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var window = new NuGetPackageManagerWindow(projectPaths, this);
        window.ShowDialog();
    }

    /// <summary>
    /// Opens the NuGet Package Manager and highlights the specified package.
    /// Called when a Package node is double-clicked in the explorer tree.
    /// </summary>
    private void OnNuGetPackageHighlightRequested(string packageId)
    {
        var projectPaths = NuGetService.GetProjectPaths(
            _viewModel.LoadedSolutionProjectPaths,
            _viewModel.LoadedProjectOrSolutionPath);

        if (projectPaths.Count == 0)
        {
            MessageBox.Show("Open a .NET project or solution first to manage NuGet packages.",
                "NuGet Package Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var window = new NuGetPackageManagerWindow(projectPaths, this, highlightPackageId: packageId);
        window.ShowDialog();
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
    /// Configures the AI Chat panel with the active provider from the registry.
    /// </summary>
    private void ConfigureAiChatPanel()
    {
        IAiProvider? provider = _aiProviderRegistry.ActiveProvider;
        AiProviderSettings? settings = provider is null ? null : _aiProviderRegistry.GetSettings(provider);
        AiChatPanel.Configure(provider, settings?.SelectedModel);
        AiChatPanel.SetDebugLogService(_aiDebugLogService);
        AiChatPanel.SetProviderRegistry(_aiProviderRegistry);
        AiChatPanel.SetProjectItemsProvider(() => _viewModel.ProjectItems);
        AiChatPanel.SetCurrentDocumentProvider(GetCurrentDocumentSnapshot);
        AiChatPanel.SetOpenDocumentsProvider(GetOpenDocumentSnapshots);
        AiChatPanel.SetBuildOutputProvider(GetBuildOutputSnapshot);
        AiChatPanel.SetConversationProjectKeyProvider(() =>
            _viewModel.ProjectItems.FirstOrDefault(i => i.ItemType is ProjectItemType.Solution or ProjectItemType.Project)?.FullPath
            ?? _viewModel.ProjectItems.FirstOrDefault()?.FullPath);
        AiChatPanel.SetToolRegistry(_agentToolRegistry);
        AiChatPanel.SetModeRegistry(_aiChatModeRegistry);
        AiChatPanel.SetExternalContextDirectoryRegistry(_externalContextDirectoryRegistry);
        AiDebugPanel.ToolFailures = _aiDebugLogService.ToolFailures;
        AiDebugPanel.SetDebugLogService(_aiDebugLogService);
    }

    /// <summary>
    /// Registers the available AI chat modes. Application mode is registered first
    /// so it becomes the default mode when KaneCode launches. When a project is
    /// subsequently loaded, the IDE automatically switches to agent mode.
    /// </summary>
    private void RegisterAiChatModes()
    {
        // Providers for dynamic content in the Application mode system prompt
        Func<IReadOnlyList<RecentProjectItem>> recentProjectsProvider = () =>
            (IReadOnlyList<RecentProjectItem>)_viewModel.RecentProjects;

        Func<string> defaultProjectFolderProvider = () =>
            GeneralSettingsManager.LoadDefaultProjectFolder();

        _aiChatModeRegistry.Register(new ApplicationMode(recentProjectsProvider, defaultProjectFolderProvider));
        _aiChatModeRegistry.Register(new AgentMode());
        _aiChatModeRegistry.Register(new ChatMode());
        _aiChatModeRegistry.Register(new TeacherMode());
        _aiChatModeRegistry.Register(new CustomMode());
    }

    /// <summary>

    private void RegisterAgentTools()
    {
        Func<string?> projectRoot = () =>
            _viewModel.ProjectItems.FirstOrDefault(i => i.ItemType is ProjectItemType.Solution or ProjectItemType.Project)?.FullPath
            ?? _viewModel.ProjectItems.FirstOrDefault()?.FullPath;

        Action<string> onFileChanged = _viewModel.NotifyFileChangedOnDisk;

        // ── Application mode tools ─────────────────────────────────────
        // These tools work without a loaded project and are used by the
        // Application mode (the default mode when KaneCode launches).

        // Async load delegate that returns Task for proper error propagation
        Func<string, Task> loadProjectAsync = path => _viewModel.OpenProjectByPathAsync(path);
        Func<string, Task> loadSolutionAsync = path => _viewModel.OpenSolutionByPathAsync(path);

        // Folder load delegate — called from background via the dispatcher
        Action<string> loadFolder = path =>
        {
            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
            {
                dispatcher.Invoke(() => _viewModel.LoadProjectRoot(path));
            }
        };

        // Default project folder provider
        Func<string> defaultProjectFolderProvider = () =>
            GeneralSettingsManager.LoadDefaultProjectFolder();

        _agentToolRegistry.Register(new LoadProjectTool(
            loadProjectAsync, loadSolutionAsync, loadFolder, defaultProjectFolderProvider));
        _agentToolRegistry.Register(new NewProjectTool(
            _templateEngine, loadProjectAsync, loadSolutionAsync, defaultProjectFolderProvider));

        // ── Agent mode tools ───────────────────────────────────────────
        // Read-only tools (read, list, search) use a fallback root: when no project is loaded
        // they operate from the user's default project folder. This enables Application mode
        // to browse/search files before a project is opened.
        Func<string?> projectRootWithFallback = () =>
            projectRoot() ?? defaultProjectFolderProvider();

        _agentToolRegistry.Register(new ReadFileTool(projectRootWithFallback, _externalContextDirectoryRegistry));
        _agentToolRegistry.Register(new WriteFileTool(projectRoot, onFileChanged));
        _agentToolRegistry.Register(new EditFileTool(projectRoot, onFileChanged));
        _agentToolRegistry.Register(new DeleteFileTool(projectRoot));
        _agentToolRegistry.Register(new RenamePathTool(projectRoot));
        _agentToolRegistry.Register(new CreateDirectoryTool(projectRoot));
        _agentToolRegistry.Register(new DeleteDirectoryTool(projectRoot));
        _agentToolRegistry.Register(new ListFilesTool(projectRootWithFallback, _externalContextDirectoryRegistry));
        _agentToolRegistry.Register(new SearchFilesTool(projectRootWithFallback, _externalContextDirectoryRegistry));
        _agentToolRegistry.Register(new RunCleanTool(_viewModel.BuildService, projectRoot));
        _agentToolRegistry.Register(new RunBuildTool(_viewModel.BuildService, projectRoot));
        _agentToolRegistry.Register(new RunTestTool(_viewModel.BuildService, projectRoot));
        _agentToolRegistry.Register(new RunDotnetTool(_viewModel.BuildService, projectRoot));
        _agentToolRegistry.Register(new GetDiagnosticsTool(_viewModel.RoslynService, projectRoot));
        _agentToolRegistry.Register(new PresentationNewTool(_presentationService));
        _agentToolRegistry.Register(new PresentationAddSlideTool(_presentationService, projectRoot));

        // ── Git tools ─────────────────────────────────────────────
        Func<GitService?> gitService = () => _viewModel.GitService;
        Action onRepositoryChanged = () => _viewModel.RefreshGitStatusCommand.Execute(null);

        _agentToolRegistry.Register(new GitStatusTool(gitService));
        _agentToolRegistry.Register(new GitCommitTool(gitService));
        _agentToolRegistry.Register(new GitLogTool(gitService));
        _agentToolRegistry.Register(new GitStageTool(gitService));
        _agentToolRegistry.Register(new GitUnstageTool(gitService));
        _agentToolRegistry.Register(new GitDiscardTool(gitService));
        _agentToolRegistry.Register(new GitDiffTool(gitService));
        _agentToolRegistry.Register(new GitBranchesTool(gitService));
        _agentToolRegistry.Register(new GitCreateBranchTool(gitService));
        _agentToolRegistry.Register(new GitDeleteBranchTool(gitService));
        _agentToolRegistry.Register(new GitCheckoutTool(gitService));
        _agentToolRegistry.Register(new GitFetchTool(gitService));
        _agentToolRegistry.Register(new GitPullTool(gitService));
        _agentToolRegistry.Register(new GitPushTool(gitService));
        _agentToolRegistry.Register(new GitConflictsTool(gitService));
        _agentToolRegistry.Register(new GitResolveConflictTool(gitService));
        _agentToolRegistry.Register(new GitInitTool(projectRoot, gitService, onRepositoryChanged));
        _agentToolRegistry.Register(new GitHeadFileTool(gitService));

        // ── NuGet tools ──────────────────────────────────────────────
        _agentToolRegistry.Register(new NuGetSearchTool(_nuGetService));
        _agentToolRegistry.Register(new NuGetInfoTool(_nuGetService));
        _agentToolRegistry.Register(new NuGetListInstalledTool(projectRoot));
        _agentToolRegistry.Register(new NuGetInstallTool(projectRoot));
        _agentToolRegistry.Register(new NuGetUninstallTool(projectRoot));
    }

    private AiContextDocumentSnapshot? GetCurrentDocumentSnapshot()
    {
        OpenFileTab? activeTab = _viewModel.ActiveTab;
        if (activeTab is null || string.IsNullOrWhiteSpace(activeTab.FilePath))
        {
            return null;
        }

        return new AiContextDocumentSnapshot(activeTab.FilePath, activeTab.DisplayName, activeTab.Document.Text);
    }

    private IReadOnlyList<AiContextDocumentSnapshot> GetOpenDocumentSnapshots()
    {
        List<AiContextDocumentSnapshot> snapshots = [];

        foreach (OpenFileTab openTab in _viewModel.OpenTabs)
        {
            snapshots.Add(new AiContextDocumentSnapshot(openTab.FilePath, openTab.DisplayName, openTab.Document.Text));
        }

        return snapshots;
    }

    private AiBuildOutputSnapshot? GetBuildOutputSnapshot()
    {
        if (string.IsNullOrWhiteSpace(_viewModel.BuildSummary) && _viewModel.BuildOutputLines.Count == 0)
        {
            return null;
        }

        return new AiBuildOutputSnapshot(_viewModel.BuildSummary, _viewModel.BuildOutputLines.ToList());
    }

    /// <summary>
    /// Binds the presentation overlay to the service and handles navigation events.
    /// </summary>
    private void WirePresentationOverlay()
    {
        PresentationOverlay.Bind(_presentationService);
        PresentationOverlay.NavigateRequested += (_, slide) =>
        {
            _viewModel.NavigateToFileLine(slide.FilePath, slide.Line);
            UpdatePresentationLineHighlight();
        };

        PresentationOverlay.CloseRequested += (_, _) =>
        {
            UpdatePresentationLineHighlight();
        };
    }

    private void UpdatePresentationLineHighlight()
    {
        PresentationSlide? currentSlide = _presentationService.CurrentSlide;
        OpenFileTab? activeTab = _viewModel.ActiveTab;

        if (currentSlide is null ||
            activeTab is null ||
            !string.Equals(activeTab.FilePath, currentSlide.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            _presentationLineHighlightRenderer.SetHighlightedLine(0);
            CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
            return;
        }

        _presentationLineHighlightRenderer.SetHighlightedLine(currentSlide.Line);
        CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    /// <summary>
    /// Reloads AI providers and reconfigures the chat panel. Call after settings change.
    /// </summary>
    internal void ReloadAiProviders()
    {
        _aiProviderRegistry.Reload();
        ConfigureAiChatPanel();
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
        AddWindowBinding(HotkeyAction.OpenProjectOrSolution, _viewModel.OpenProjectOrSolutionCommand);
        AddWindowBinding(HotkeyAction.Save, _viewModel.SaveCommand);
        AddWindowBinding(HotkeyAction.SaveAs, _viewModel.SaveAsCommand);
        AddWindowBinding(HotkeyAction.CloseTab, _viewModel.CloseTabCommand);
        AddWindowBinding(HotkeyAction.GoToSymbol, _viewModel.GoToSymbolCommand);
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

        // Override AvalonEdit default Ctrl+D (delete line) with duplicate line behavior.
        // Must remove AvalonEdit's built-in binding from TextArea first,
        // then add ours to the TextArea so it intercepts at the right level.
        var textArea = CodeEditor.TextArea;
        for (var i = textArea.InputBindings.Count - 1; i >= 0; i--)
        {
            if (textArea.InputBindings[i] is KeyBinding kb &&
                kb.Key == Key.D && kb.Modifiers == ModifierKeys.Control)
            {
                textArea.InputBindings.RemoveAt(i);
            }
        }

        textArea.InputBindings.Add(new KeyBinding(
            new RelayInputCommand(() =>
            {
                DuplicateCurrentLine();
                return Task.CompletedTask;
            }),
            Key.D,
            ModifierKeys.Control));

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

    private void MarkdownViewToggle_Checked(object sender, RoutedEventArgs e)
    {
        // Guard: during XAML initialization the named elements may not be
        // resolved yet (IsChecked=True triggers the Checked event on start).
        if (!IsLoaded)
        {
            return;
        }

        if (sender == MarkdownEditButton)
        {
            MarkdownPreviewButton.IsChecked = false;
            ShowMarkdownEditorView();
        }
        else if (sender == MarkdownPreviewButton)
        {
            MarkdownEditButton.IsChecked = false;
            ShowMarkdownPreviewView();
        }
    }

    private void MarkdownViewToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        // Guard: during XAML initialization the named elements may not be
        // resolved yet (IsChecked changes can trigger Unchecked on start).
        if (!IsLoaded)
        {
            return;
        }

        // When one toggle is unchecked, we ensure the other is checked
        // to keep exactly one active. This is handled in the Checked handler.
    }

    private void ShowMarkdownEditorView()
    {
        CodeEditor.Visibility = Visibility.Visible;
        MarkdownPreview.Visibility = Visibility.Collapsed;

        if (_viewModel.ActiveTab is not null)
        {
            _viewModel.ActiveTab.IsMarkdownPreviewActive = false;
        }
    }

    private void ShowMarkdownPreviewView()
    {
        CodeEditor.Visibility = Visibility.Collapsed;
        MarkdownPreview.Visibility = Visibility.Visible;

        if (_viewModel.ActiveTab is not null)
        {
            _viewModel.ActiveTab.IsMarkdownPreviewActive = true;
        }

        // Refresh the preview with the latest editor content
        MarkdownPreview.SetMarkdownContent(CodeEditor.Text);
    }

    /// <summary>
    /// Updates the markdown toolbar visibility based on the active tab's file extension.
    /// Restores the tab's saved preview state (Edit vs Preview) when switching back.
    /// </summary>
    private void UpdateMarkdownToolbar()
    {
        var activeTab = _viewModel.ActiveTab;
        bool isMarkdownFile = activeTab?.FilePath is not null &&
            Path.GetExtension(activeTab.FilePath)?.Equals(".md", StringComparison.OrdinalIgnoreCase) == true;

        MarkdownToolbar.Visibility = isMarkdownFile ? Visibility.Visible : Visibility.Collapsed;

        if (isMarkdownFile && activeTab is not null)
        {
            // Restore the tab's saved preview state
            if (activeTab.IsMarkdownPreviewActive)
            {
                if (MarkdownPreviewButton.IsChecked != true)
                {
                    MarkdownPreviewButton.IsChecked = true;
                }
                else
                {
                    ShowMarkdownPreviewView();
                }
            }
            else
            {
                if (MarkdownEditButton.IsChecked != true)
                {
                    MarkdownEditButton.IsChecked = true;
                }
                else
                {
                    ShowMarkdownEditorView();
                }
            }
        }
        else
        {
            // Ensure editor is visible for non-markdown files
            ShowMarkdownEditorView();
        }
    }

    /// <summary>
    /// Called whenever the editor text changes. Updates the markdown preview
    /// if preview mode is currently active.
    /// </summary>
    private void OnCodeEditorTextChanged(object? sender, EventArgs e)
    {
        if (MarkdownPreview.Visibility == Visibility.Visible)
        {
            MarkdownPreview.SetMarkdownContent(CodeEditor.Text);
        }
    }

    private void DuplicateCurrentLine()
    {
        if (CodeEditor.Document is null)
        {
            return;
        }

        var document = CodeEditor.Document;
        var caretOffset = CodeEditor.CaretOffset;
        var line = document.GetLineByOffset(caretOffset);
        var columnInLine = caretOffset - line.Offset;

        var lineText = document.GetText(line.Offset, line.Length);
        var delimiter = line.DelimiterLength > 0
            ? document.GetText(line.EndOffset, line.DelimiterLength)
            : Environment.NewLine;

        var insertOffset = line.EndOffset + line.DelimiterLength;
        var duplicatedText = lineText + delimiter;

        document.BeginUpdate();
        try
        {
            document.Insert(insertOffset, duplicatedText);
        }
        finally
        {
            document.EndUpdate();
        }

        var duplicatedLine = document.GetLineByNumber(line.LineNumber + 1);
        var newCaret = duplicatedLine.Offset + Math.Min(columnInLine, duplicatedLine.Length);
        CodeEditor.CaretOffset = newCaret;
        CodeEditor.TextArea.Caret.BringCaretToView();
    }

    /// <summary>
    /// Walks the menu tree and updates InputGestureText for items whose Header
    /// matches a known hotkey action.
    /// </summary>
    private void UpdateMenuGestureText()
    {
        foreach (var topItem in MainMenu.Items.OfType<MenuItem>())
        {
            UpdateMenuItemGestures(topItem);
        }
    }

    private static readonly Dictionary<string, HotkeyAction> s_menuHeaderToAction = new(StringComparer.OrdinalIgnoreCase)
    {
        ["_New"] = HotkeyAction.NewFile,
        ["_Open File"] = HotkeyAction.OpenFile,
        ["Open _Folder"] = HotkeyAction.OpenFolder,
        ["Open _Project or Solution..."] = HotkeyAction.OpenProjectOrSolution,
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
        ["Go To _Symbol..."] = HotkeyAction.GoToSymbol,
        ["Code _Actions"] = HotkeyAction.CodeActions,
        ["_Rename"] = HotkeyAction.Rename,
        ["_Extract Method"] = HotkeyAction.ExtractMethod,
        ["_Options"] = HotkeyAction.OpenOptions,
        ["E_xit"] = HotkeyAction.Exit,
        // Build menu header is now dynamic (BuildSelectedText binding), so no static lookup
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

    // ── Tab drag-drop reordering ──────────────────────────────────────

    private Point _tabDragStartPoint;
    private bool _isTabDragging;

    private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is TabControl tabControl && tabControl.SelectedItem is OpenFileTab tab)
        {
            _viewModel.SwitchToTab(tab);
        }
    }

    private void TabItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragStartPoint = e.GetPosition(null);
        _isTabDragging = false;
    }

    private void TabItem_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point position = e.GetPosition(null);
        Vector diff = _tabDragStartPoint - position;

        if (!_isTabDragging && (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance))
        {
            _isTabDragging = true;

            if (sender is TabItem tabItem && tabItem.DataContext is OpenFileTab draggedTab)
            {
                DragDrop.DoDragDrop(tabItem, draggedTab, DragDropEffects.Move);
            }
        }
    }

    /// <summary>
    /// Shows a drag-drop cursor while hovering over the tab strip.
    /// </summary>
    private void TabStrip_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(OpenFileTab)) is OpenFileTab)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    /// <summary>
    /// Handles dropping a tab into a new position in the tab strip.
    /// </summary>
    private void TabStrip_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(OpenFileTab)) is not OpenFileTab draggedTab)
        {
            return;
        }

        // Find the tab item under the drop position
        TabControl tabControl = (TabControl)sender;
        Point dropPosition = e.GetPosition(tabControl);

        // Get the first TabItem's position and width to determine insertion index
        int targetIndex = -1;
        double accumulatedWidth = 0;

        for (int i = 0; i < tabControl.Items.Count; i++)
        {
            if (tabControl.ItemContainerGenerator.ContainerFromIndex(i) is TabItem tabItem)
            {
                double itemWidth = tabItem.ActualWidth;
                double itemMidpoint = accumulatedWidth + itemWidth / 2;

                if (dropPosition.X < itemMidpoint)
                {
                    targetIndex = i;
                    break;
                }

                accumulatedWidth += itemWidth;
            }
        }

        if (targetIndex < 0)
        {
            // Drop at the end — use the last valid index since ObservableCollection.Move
            // requires both indices to be in range (0 to Count-1) after removal.
            targetIndex = tabControl.Items.Count;
        }

        int currentIndex = _viewModel.OpenTabs.IndexOf(draggedTab);
        if (currentIndex < 0 || currentIndex == targetIndex)
        {
            return;
        }

        _viewModel.MoveTab(currentIndex, targetIndex);
        e.Handled = true;
    }

    private void ErrorList_NavigateRequested(object? sender, DiagnosticItem item)
    {
        _viewModel.NavigateToDiagnostic(item);
    }

    private async void ErrorList_FixRequested(object? sender, DiagnosticItem item)
    {
        await _viewModel.ApplyDiagnosticFixAsync(item);
    }

    private void FindReferencesPanel_NavigateRequested(object? sender, ReferenceItem item)
    {
        _viewModel.NavigateToReference(item);
    }

    private void FindReferencesPanel_PreviewRequested(object? sender, ReferenceItem? item)
    {
        _viewModel.UpdateReferencePeek(item);
    }

    private void GitChangesPanel_RefreshRequested(object? sender, EventArgs e)
    {
        _viewModel.RefreshGitStatusCommand.Execute(null);
    }

    private void GitChangesPanel_FileOpenRequested(object? sender, GitChangesEntry entry)
    {
        _viewModel.OpenFileByPath(entry.FullPath);
    }

    private void GitChangesPanel_StageRequested(object? sender, GitChangesEntry entry)
    {
        _viewModel.StageFileCommand.Execute(entry);
    }

    private void GitChangesPanel_StageAllRequested(object? sender, EventArgs e)
    {
        _viewModel.StageAllCommand.Execute(null);
    }

    private void GitChangesPanel_UnstageRequested(object? sender, GitChangesEntry entry)
    {
        _viewModel.UnstageFileCommand.Execute(entry);
    }

    private void GitChangesPanel_UnstageAllRequested(object? sender, EventArgs e)
    {
        _viewModel.UnstageAllCommand.Execute(null);
    }

    private void GitChangesPanel_DiscardRequested(object? sender, GitChangesEntry entry)
    {
        _viewModel.DiscardFileCommand.Execute(entry);
    }

    private async void GitChangesPanel_DiffRequested(object? sender, GitChangesEntry entry)
    {
        var diff = await _viewModel.GetFileDiffAsync(entry.RelativePath);
        if (diff is null)
        {
            return;
        }

        GitDiffPanel.SetDiff(diff.RelativePath, diff.OriginalText, diff.ModifiedText);
        ShowLayoutAnchorable(GitDiffAnchorable);
    }

    private void GitChangesPanel_AcceptCurrentConflictRequested(object? sender, GitChangesEntry entry)
    {
        _viewModel.AcceptCurrentConflictCommand.Execute(entry);
    }

    private void GitChangesPanel_AcceptIncomingConflictRequested(object? sender, GitChangesEntry entry)
    {
        _viewModel.AcceptIncomingConflictCommand.Execute(entry);
    }

    private void GitChangesPanel_AcceptBothConflictRequested(object? sender, GitChangesEntry entry)
    {
        _viewModel.AcceptBothConflictCommand.Execute(entry);
    }

    private async void TextView_MouseHover(object? sender, MouseEventArgs e)
    {
        if (_isQuickInfoPinned)
        {
            return;
        }

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

        ShowQuickInfoPopup(result, e.GetPosition(this));
    }

    private void TextView_MouseHoverStopped(object? sender, MouseEventArgs e)
    {
        if (!_isQuickInfoPinned)
        {
            CloseQuickInfoPopup();
        }
    }

    private void TextView_VisualLinesChanged(object? sender, EventArgs e)
    {
        if (!_isQuickInfoPinned)
        {
            CloseQuickInfoPopup();
        }
    }

    private void ShowQuickInfoPopup(QuickInfoResult result, Point position)
    {
        CloseQuickInfoPopup();

        var border = new Border
        {
            Padding = new Thickness(8, 6, 8, 6),
            MaxWidth = 600,
            CornerRadius = new CornerRadius(3)
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

        Brush? defaultForeground = Application.Current.TryFindResource(ThemeResourceKeys.TooltipForeground) as Brush;

        var contentPanel = new StackPanel();

        // Render Roslyn quick info sections as syntax-colored text
        foreach (var section in result.Sections)
        {
            var textBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                FontSize = 12,
                Margin = contentPanel.Children.Count > 0 ? new Thickness(0, 4, 0, 0) : default
            };

            if (defaultForeground is not null)
            {
                textBlock.Foreground = defaultForeground;
            }

            foreach (var part in section.TaggedParts)
            {
                var run = new Run(part.Text);
                string? themeKey = RoslynQuickInfoService.GetThemeKeyForTag(part.Tag);
                if (themeKey is not null && Application.Current.TryFindResource(themeKey) is Brush partBrush)
                {
                    run.Foreground = partBrush;
                }

                textBlock.Inlines.Add(run);
            }

            contentPanel.Children.Add(textBlock);
        }

        // Render diagnostic messages with severity coloring
        if (result.Diagnostics.Count > 0)
        {
            if (contentPanel.Children.Count > 0)
            {
                contentPanel.Children.Add(new Separator
                {
                    Margin = new Thickness(0, 4, 0, 4),
                    Opacity = 0.3
                });
            }

            foreach (var diag in result.Diagnostics)
            {
                var diagBlock = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                    FontSize = 11.5,
                    Margin = new Thickness(0, 1, 0, 1)
                };

                string severityIcon = diag.Severity switch
                {
                    Microsoft.CodeAnalysis.DiagnosticSeverity.Error => "\u274C ",
                    Microsoft.CodeAnalysis.DiagnosticSeverity.Warning => "\u26A0\uFE0F ",
                    _ => "\u2139\uFE0F "
                };

                string severityThemeKey = diag.Severity switch
                {
                    Microsoft.CodeAnalysis.DiagnosticSeverity.Error => ThemeResourceKeys.DiagnosticErrorForeground,
                    Microsoft.CodeAnalysis.DiagnosticSeverity.Warning => ThemeResourceKeys.DiagnosticWarningForeground,
                    _ => ThemeResourceKeys.DiagnosticInfoForeground
                };

                var iconRun = new Run(severityIcon);
                var idRun = new Run($"{diag.Id}: ") { FontWeight = FontWeights.Bold };
                var msgRun = new Run(diag.Message);

                if (Application.Current.TryFindResource(severityThemeKey) is Brush diagBrush)
                {
                    idRun.Foreground = diagBrush;
                    iconRun.Foreground = diagBrush;
                }

                if (defaultForeground is not null)
                {
                    msgRun.Foreground = defaultForeground;
                }

                diagBlock.Inlines.Add(iconRun);
                diagBlock.Inlines.Add(idRun);
                diagBlock.Inlines.Add(msgRun);
                contentPanel.Children.Add(diagBlock);
            }
        }

        // Pin and Copy toolbar
        var plainText = result.ToPlainText();
        var toolbarPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var pinButton = new Button
        {
            Content = "\uD83D\uDCCC",
            ToolTip = "Pin tooltip",
            FontSize = 10,
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        pinButton.Click += (_, _) =>
        {
            _isQuickInfoPinned = !_isQuickInfoPinned;
            pinButton.Content = _isQuickInfoPinned ? "\uD83D\uDCCC\u2714" : "\uD83D\uDCCC";
            pinButton.ToolTip = _isQuickInfoPinned ? "Unpin tooltip" : "Pin tooltip";
        };

        var copyButton = new Button
        {
            Content = "\uD83D\uDCCB",
            ToolTip = "Copy to clipboard",
            FontSize = 10,
            Padding = new Thickness(4, 1, 4, 1),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        copyButton.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(plainText);
                copyButton.Content = "\u2714";
            }
            catch
            {
                // Clipboard access can fail if locked by another process
            }
        };

        if (defaultForeground is not null)
        {
            pinButton.Foreground = defaultForeground;
            copyButton.Foreground = defaultForeground;
        }

        toolbarPanel.Children.Add(pinButton);
        toolbarPanel.Children.Add(copyButton);
        contentPanel.Children.Add(toolbarPanel);

        border.Child = contentPanel;

        _quickInfoPopup = new Popup
        {
            Child = border,
            Placement = PlacementMode.Relative,
            PlacementTarget = this,
            HorizontalOffset = position.X,
            VerticalOffset = position.Y + 16,
            AllowsTransparency = true,
            StaysOpen = true,
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

        _isQuickInfoPinned = false;
    }

    // ── Inline Rename ──────────────────────────────────────────────────

    private void OnInlineRenameRequested(InlineRenameSession session)
    {
        // Subscribe to preview data for the affected-files popup
        session.PreviewReady += OnRenamePreviewReady;
        session.Cancelled += OnRenameSessionEnded;
        session.Committed += OnRenameSessionCommitted;
    }

    private void OnRenamePreviewReady(object? sender, IReadOnlyList<RenamePreviewItem> items)
    {
        if (items.Count <= 1)
        {
            // Single-file rename: no preview needed
            return;
        }

        ShowRenamePreviewPopup(items);
    }

    private void OnRenameSessionCommitted(object? sender, InlineRenameCommitArgs args)
    {
        CleanupRenameSessionHandlers(sender as InlineRenameSession);
        CloseRenamePreviewPopup();
        CodeEditor.TextArea.Focus();
    }

    private void OnRenameSessionEnded(object? sender, EventArgs e)
    {
        CleanupRenameSessionHandlers(sender as InlineRenameSession);
        CloseRenamePreviewPopup();
        CodeEditor.TextArea.Focus();
    }

    private static void CleanupRenameSessionHandlers(InlineRenameSession? session)
    {
        if (session is null)
        {
            return;
        }

        session.PreviewReady -= null; // detach is best-effort; session is being disposed
    }

    /// <summary>
    /// Shows a small popup near the editor caret listing the files that will be affected by the rename.
    /// </summary>
    private void ShowRenamePreviewPopup(IReadOnlyList<RenamePreviewItem> items)
    {
        CloseRenamePreviewPopup();

        var panel = new StackPanel { Margin = new Thickness(8) };

        var header = new TextBlock
        {
            Text = $"Rename will affect {items.Count} file(s):",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        };
        panel.Children.Add(header);

        foreach (RenamePreviewItem item in items)
        {
            var line = new TextBlock
            {
                Text = $"  {item.FileName}  ({item.OccurrenceCount} occurrence{(item.OccurrenceCount != 1 ? "s" : "")})",
                Margin = new Thickness(0, 1, 0, 1)
            };
            panel.Children.Add(line);
        }

        Brush? bg = Application.Current.TryFindResource(ThemeResourceKeys.RenamePreviewBackground) as Brush;
        Brush? fg = Application.Current.TryFindResource(ThemeResourceKeys.RenamePreviewForeground) as Brush;
        Brush? border = Application.Current.TryFindResource(ThemeResourceKeys.RenamePreviewBorder) as Brush;

        if (fg is not null)
        {
            header.Foreground = fg;
            foreach (var child in panel.Children.OfType<TextBlock>())
            {
                child.Foreground = fg;
            }
        }

        var container = new Border
        {
            Child = panel,
            Background = bg ?? Brushes.White,
            BorderBrush = border ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 6,
                ShadowDepth = 2,
                Opacity = 0.3
            }
        };

        // Position near the caret
        var caretPos = CodeEditor.TextArea.TextView.GetVisualPosition(
            CodeEditor.TextArea.Caret.Position,
            ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineBottom);
        var screenPoint = CodeEditor.TextArea.TextView.PointToScreen(caretPos);
        var windowPoint = PointFromScreen(screenPoint);

        _renamePreviewPopup = new Popup
        {
            Child = container,
            Placement = PlacementMode.Relative,
            PlacementTarget = this,
            HorizontalOffset = windowPoint.X,
            VerticalOffset = windowPoint.Y + 24,
            AllowsTransparency = true,
            StaysOpen = true,
            IsOpen = true
        };
    }

    private void CloseRenamePreviewPopup()
    {
        if (_renamePreviewPopup is not null)
        {
            _renamePreviewPopup.IsOpen = false;
            _renamePreviewPopup = null;
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
            Text = GeneralSettingsManager.LoadDefaultProjectFolder(),
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

        AddLabel(rootPanel, "Project path:", 4);
        TextBlock projectPathTextBlock = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 8),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(projectPathTextBlock, 4);
        Grid.SetColumn(projectPathTextBlock, 1);
        Grid.SetColumnSpan(projectPathTextBlock, 2);
        rootPanel.Children.Add(projectPathTextBlock);

        void RefreshProjectPathPreview()
        {
            string location = destinationTextBox.Text.Trim();
            string name = nameTextBox.Text.Trim();
            projectPathTextBlock.Text = string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(name)
                ? string.Empty
                : Path.Combine(location, name);
        }

        nameTextBox.TextChanged += (_, _) => RefreshProjectPathPreview();
        destinationTextBox.TextChanged += (_, _) => RefreshProjectPathPreview();
        RefreshProjectPathPreview();

        var createSolutionCheckBox = new CheckBox
        {
            Content = "Create solution (.sln)",
            IsChecked = true,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(createSolutionCheckBox, 5);
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

    private void EditorContextMenu_AskAboutSelection(object sender, RoutedEventArgs e)
    {
        var selection = CodeEditor.SelectedText;
        if (string.IsNullOrWhiteSpace(selection))
        {
            MessageBox.Show("Select some code first, then try 'Ask AI About Selection'.", "No Selection",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var filePath = _viewModel.ActiveTab?.FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            MessageBox.Show("No active file is open.", "Ask AI About Selection",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectionStart = CodeEditor.SelectionStart;
        var selectionEnd = selectionStart + CodeEditor.SelectionLength;

        var diagnostics = _viewModel.DiagnosticItems
            .Where(d => d.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.EndOffset >= selectionStart && d.StartOffset <= selectionEnd)
            .OrderBy(d => d.StartOffset)
            .Take(20)
            .ToList();

        AiChatPanel.AskAboutSelection(filePath, selection, diagnostics);
        ShowLayoutAnchorable(AiChatAnchorable);
        AiChatPanel.FocusInput();
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

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeSelector.SelectedItem is not ThemeOption selected)
            return;

        _themeManager.CurrentTheme = selected;
        DockManager.Theme = selected.AvalonDockTheme;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Maximized;

    private void OnRestoreDownClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Normal;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}