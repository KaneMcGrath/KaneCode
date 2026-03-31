using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Search;
using KaneCode.Infrastructure;
using KaneCode.Models;
using KaneCode.Services;
using KaneCode.Theming;
using Microsoft.CodeAnalysis;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KaneCode.ViewModels;

/// <summary>
/// Main view model that orchestrates project loading, file opening, editing, and saving.
/// </summary>
internal sealed class MainViewModel : ObservableObject, IDisposable
{
    private TextEditor? _editor;
    private bool _isActivating;

    private readonly RoslynWorkspaceService _roslynService = new();
    private readonly RoslynCompletionProvider _completionProvider;
    private readonly RoslynNavigationService _navigationService;
    private readonly RoslynQuickInfoService _quickInfoService;
    private readonly RoslynSignatureHelpService _signatureHelpService;
    private readonly RoslynCodeActionService _codeActionService;
    private readonly RoslynRefactoringService _refactoringService;
    private readonly BuildService _buildService = new();
    private readonly GitService _gitService = new();

    /// <summary>Exposes the build service for agent tool registration.</summary>
    internal BuildService BuildService => _buildService;
    /// <summary>Exposes the Roslyn workspace service for agent tool registration.</summary>
    internal RoslynWorkspaceService RoslynService => _roslynService;
    private readonly TemplateService _templateService = new();
    private RoslynClassificationColorizer? _classificationColorizer;
    private RoslynDiagnosticRenderer? _diagnosticRenderer;
    private GitGutterChangeRenderer? _gitGutterRenderer;
    private SearchPanel? _searchPanel;
    private CompletionWindow? _completionWindow;
    private OverloadInsightWindow? _insightWindow;
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource? _navigationCts;
    private CancellationTokenSource? _findReferencesCts;
    private CancellationTokenSource? _quickInfoCts;
    private CancellationTokenSource? _signatureHelpCts;
    private CancellationTokenSource? _codeActionsCts;
    private CancellationTokenSource? _renameCts;
    private CancellationTokenSource? _loadCts;
    private FileSystemWatcher? _explorerWatcher;
    private FileSystemWatcher? _projectFileWatcher;
    private CancellationTokenSource? _explorerRefreshCts;
    private CancellationTokenSource? _projectFileRefreshCts;
    private bool _isLoadingProject;
    private bool _isUpdatingSelectedBranch;
    private readonly TimeSpan _analysisDelay = TimeSpan.FromMilliseconds(500);
    private CancellationTokenSource? _loadingStatusClearCts;
    private string? _loadedProjectOrSolutionPath;

    public MainViewModel()
    {
        ThemeManager.ThemeChanged += OnThemeChanged;
        _completionProvider = new RoslynCompletionProvider(_roslynService);
        _navigationService = new RoslynNavigationService(_roslynService);
        _quickInfoService = new RoslynQuickInfoService(_roslynService);
        _signatureHelpService = new RoslynSignatureHelpService(_roslynService);
        _codeActionService = new RoslynCodeActionService(_roslynService);
        _refactoringService = new RoslynRefactoringService(_roslynService);

        NewFileCommand = new RelayCommand(_ => NewFile());
        OpenFileCommand = new RelayCommand(_ => OpenFile());
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());
        OpenProjectCommand = new RelayCommand(_ => OpenProject(), _ => !_isLoadingProject);
        OpenSolutionCommand = new RelayCommand(_ => OpenSolution(), _ => !_isLoadingProject);
        SaveCommand = new RelayCommand(_ => Save(), _ => ActiveTab is not null);
        SaveAsCommand = new RelayCommand(_ => SaveAs(), _ => ActiveTab is not null);
        UndoCommand = new RelayCommand(_ => _editor?.Undo(), _ => _editor?.CanUndo == true);
        RedoCommand = new RelayCommand(_ => _editor?.Redo(), _ => _editor?.CanRedo == true);
        CutCommand = new RelayCommand(_ => _editor?.Cut(), _ => _editor is not null);
        CopyCommand = new RelayCommand(_ => _editor?.Copy(), _ => _editor is not null);
        PasteCommand = new RelayCommand(_ => _editor?.Paste(), _ => _editor is not null);
        FindCommand = new RelayCommand(_ => ShowFindPanel(), _ => _editor is not null);
        ReplaceCommand = new RelayCommand(_ => ShowReplacePanel(), _ => _editor is not null);
        GoToDefinitionCommand = new RelayCommand(async _ => await GoToDefinitionAsync(), _ => CanGoToDefinition());
        GoToImplementationCommand = new RelayCommand(async _ => await GoToImplementationAsync(), _ => CanGoToDefinition());
        GoToDerivedTypesCommand = new RelayCommand(async _ => await GoToDerivedTypesAsync(), _ => CanGoToDefinition());
        FindReferencesCommand = new RelayCommand(async _ => await FindReferencesAsync(), _ => CanGoToDefinition());
        CloseTabCommand = new RelayCommand(param => CloseTab(param as OpenFileTab), _ => ActiveTab is not null);
        ExitCommand = new RelayCommand(_ => ExitApplication());
        OpenOptionsCommand = new RelayCommand(_ => OpenOptions());
        BuildCommand = new RelayCommand(_ => _ = BuildProjectAsync(), _ => CanBuild());
        RunCommand = new RelayCommand(_ => _ = RunProjectAsync(), _ => CanBuild());
        CancelBuildCommand = new RelayCommand(_ => CancelBuild(), _ => _buildService.IsRunning);
        CodeActionsCommand = new RelayCommand(async _ => await ShowCodeActionsAsync(), _ => CanGoToDefinition());
        GenerateMissingMembersCommand = new RelayCommand(async _ => await GenerateMissingMembersAsync(), _ => CanGoToDefinition());
        RenameCommand = new RelayCommand(async _ => await RenameSymbolAsync(), _ => CanGoToDefinition());
        ExtractMethodCommand = new RelayCommand(async _ => await ExtractMethodAsync(), _ => CanExtractMethod());
        DeleteExplorerItemCommand = new RelayCommand(param => DeleteExplorerItem(param as ProjectItem));
        RenameExplorerItemCommand = new RelayCommand(param => RenameExplorerItem(param as ProjectItem));
        NewFolderCommand = new RelayCommand(param => CreateNewFolder(param as ProjectItem));
        RefreshGitStatusCommand = new RelayCommand(_ => RefreshGitStatus(), _ => _gitService.IsRepositoryOpen);
        InitializeGitRepositoryCommand = new RelayCommand(_ => InitializeGitRepository(), _ => CanInitializeGitRepository);
        StageFileCommand   = new RelayCommand(param => ExecuteGitOperation(() => _gitService.StageFile(AsRelativePath(param))), _ => _gitService.IsRepositoryOpen);
        StageAllCommand    = new RelayCommand(_ => ExecuteGitOperation(() => _gitService.StageAll()), _ => _gitService.IsRepositoryOpen);
        UnstageFileCommand = new RelayCommand(param => ExecuteGitOperation(() => _gitService.UnstageFile(AsRelativePath(param))), _ => _gitService.IsRepositoryOpen);
        UnstageAllCommand  = new RelayCommand(_ => ExecuteGitOperation(() => _gitService.UnstageAll()), _ => _gitService.IsRepositoryOpen);
        DiscardFileCommand = new RelayCommand(param => DiscardFile(param as GitChangesEntry), _ => _gitService.IsRepositoryOpen);
        AcceptCurrentConflictCommand = new RelayCommand(param => ResolveConflict(param as GitChangesEntry, GitConflictResolution.AcceptCurrent), _ => _gitService.IsRepositoryOpen);
        AcceptIncomingConflictCommand = new RelayCommand(param => ResolveConflict(param as GitChangesEntry, GitConflictResolution.AcceptIncoming), _ => _gitService.IsRepositoryOpen);
        AcceptBothConflictCommand = new RelayCommand(param => ResolveConflict(param as GitChangesEntry, GitConflictResolution.AcceptBoth), _ => _gitService.IsRepositoryOpen);
        CommitCommand = new RelayCommand(async _ => await CommitChangesAsync(), _ => _gitService.IsRepositoryOpen);
        CreateBranchCommand = new RelayCommand(async _ => await CreateBranchAsync(), _ => _gitService.IsRepositoryOpen);
        DeleteBranchCommand = new RelayCommand(async _ => await DeleteBranchAsync(), _ => _gitService.IsRepositoryOpen);
        FetchCommand = new RelayCommand(async _ => await FetchAsync(), _ => _gitService.IsRepositoryOpen);
        PullCommand = new RelayCommand(async _ => await PullAsync(), _ => _gitService.IsRepositoryOpen);
        PushCommand = new RelayCommand(async _ => await PushAsync(), _ => _gitService.IsRepositoryOpen);

        _buildService.OutputReceived += OnBuildOutputReceived;
        _buildService.ProcessExited += OnBuildProcessExited;
        _gitService.StatusChanged += OnGitStatusChanged;
    }

    public ICommand NewFileCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand OpenSolutionCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand CutCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand FindCommand { get; }
    public ICommand ReplaceCommand { get; }
    public ICommand GoToDefinitionCommand { get; }
    public ICommand GoToImplementationCommand { get; }
    public ICommand GoToDerivedTypesCommand { get; }
    public ICommand FindReferencesCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand OpenOptionsCommand { get; }
    public ICommand BuildCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand CancelBuildCommand { get; }
    public ICommand CodeActionsCommand { get; }
    public ICommand GenerateMissingMembersCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand ExtractMethodCommand { get; }
    public ICommand DeleteExplorerItemCommand { get; }
    public ICommand RenameExplorerItemCommand { get; }
    public ICommand NewFolderCommand { get; }
    public ICommand RefreshGitStatusCommand { get; }
    public ICommand InitializeGitRepositoryCommand { get; }
    public ICommand StageFileCommand { get; }
    public ICommand StageAllCommand { get; }
    public ICommand UnstageFileCommand { get; }
    public ICommand UnstageAllCommand { get; }
    public ICommand DiscardFileCommand { get; }
    public ICommand AcceptCurrentConflictCommand { get; }
    public ICommand AcceptIncomingConflictCommand { get; }
    public ICommand AcceptBothConflictCommand { get; }
    public ICommand CommitCommand { get; }
    public ICommand CreateBranchCommand { get; }
    public ICommand DeleteBranchCommand { get; }
    public ICommand FetchCommand { get; }
    public ICommand PullCommand { get; }
    public ICommand PushCommand { get; }

    private ObservableCollection<ProjectItem> _projectItems = [];
    public ObservableCollection<ProjectItem> ProjectItems
    {
        get => _projectItems;
        private set => SetProperty(ref _projectItems, value);
    }

    private string? _projectRootPath;
    public string? ProjectRootPath
    {
        get => _projectRootPath;
        private set
        {
            if (SetProperty(ref _projectRootPath, value))
            {
                OnPropertyChanged(nameof(WindowTitle));
                OnPropertyChanged(nameof(CanInitializeGitRepository));
            }
        }
    }

    private string? _loadingStatus;
    /// <summary>Status text shown during project/solution loading.</summary>
    public string? LoadingStatus
    {
        get => _loadingStatus;
        private set
        {
            if (SetProperty(ref _loadingStatus, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public ObservableCollection<OpenFileTab> OpenTabs { get; } = [];

    /// <summary>
    /// Output lines from build/run processes, displayed in the Build Output panel.
    /// </summary>
    public ObservableCollection<string> BuildOutputLines { get; } = [];

    private string _buildSummary = string.Empty;
    /// <summary>Summary text shown in the Build Output panel header.</summary>
    public string BuildSummary
    {
        get => _buildSummary;
        private set => SetProperty(ref _buildSummary, value);
    }

    /// <summary>
    /// Diagnostics shown in the error list panel, updated after each analysis pass.
    /// </summary>
    public ObservableCollection<DiagnosticItem> DiagnosticItems { get; } = [];

    /// <summary>
    /// References shown in the Find References panel.
    /// </summary>
    public ObservableCollection<ReferenceItem> ReferenceItems { get; } = [];

    private string _findReferencesStatusText = string.Empty;
    /// <summary>Status text shown in the Find References panel header.</summary>
    public string FindReferencesStatusText
    {
        get => _findReferencesStatusText;
        private set => SetProperty(ref _findReferencesStatusText, value);
    }

    /// <summary>Files with unstaged working-directory changes, shown in the Git Changes panel.</summary>
    public ObservableCollection<GitChangesEntry> UnstagedChanges { get; } = [];

    /// <summary>Files with staged index changes, shown in the Git Changes panel.</summary>
    public ObservableCollection<GitChangesEntry> StagedChanges { get; } = [];

    /// <summary>Commit history rows shown in the Git Log panel.</summary>
    public ObservableCollection<GitCommitLogItem> GitCommitHistory { get; } = [];

    /// <summary>Local Git branches available for checkout, shown in the Git Changes panel branch selector.</summary>
    public ObservableCollection<string> GitBranches { get; } = [];

    private string _gitChangesStatusText = string.Empty;
    /// <summary>Summary text shown in the Git Changes panel header.</summary>
    public string GitChangesStatusText
    {
        get => _gitChangesStatusText;
        private set => SetProperty(ref _gitChangesStatusText, value);
    }

    private string _gitLogStatusText = string.Empty;
    /// <summary>Summary text shown in the Git Log panel header.</summary>
    public string GitLogStatusText
    {
        get => _gitLogStatusText;
        private set => SetProperty(ref _gitLogStatusText, value);
    }

    public bool CanInitializeGitRepository => !string.IsNullOrWhiteSpace(ProjectRootPath) && !_gitService.IsRepositoryOpen;

    public string GitRepositoryStatusText => _gitService.IsRepositoryOpen
        ? $"Branch: {_gitService.CurrentBranchName ?? "(detached)"}"
        : "No repository";

    private string? _selectedGitBranch;
    public string? SelectedGitBranch
    {
        get => _selectedGitBranch;
        set
        {
            if (!SetProperty(ref _selectedGitBranch, value) || _isUpdatingSelectedBranch)
            {
                return;
            }

            _ = CheckoutSelectedBranchAsync(value);
        }
    }

    private OpenFileTab? _activeTab;
    public OpenFileTab? ActiveTab
    {
        get => _activeTab;
        set
        {
            if (SetProperty(ref _activeTab, value))
            {
                OnPropertyChanged(nameof(WindowTitle));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string WindowTitle
    {
        get
        {
            var parts = new List<string> { "Kane Code" };
            if (!string.IsNullOrEmpty(ProjectRootPath))
            {
                parts.Add(Path.GetFileName(ProjectRootPath));
            }

            if (ActiveTab is not null)
            {
                parts.Add(ActiveTab.DisplayName);
            }

            return string.Join(" — ", parts);
        }
    }

    public string StatusText
    {
        get
        {
            if (!string.IsNullOrEmpty(_loadingStatus))
            {
                return _loadingStatus;
            }

            if (ActiveTab is null)
            {
                return "Ready";
            }

            var status = $"Editing: {ActiveTab.FilePath}";
            if (!string.IsNullOrEmpty(_diagnosticStatusText))
            {
                status += $"  |  {_diagnosticStatusText}";
            }

            return status;
        }
    }

    public void AttachEditor(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editor = editor;
        _editor.TextChanged += OnEditorTextChanged;

        _classificationColorizer = new RoslynClassificationColorizer(_roslynService);
        _editor.TextArea.TextView.LineTransformers.Add(_classificationColorizer);

        _diagnosticRenderer = new RoslynDiagnosticRenderer();
        _editor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticRenderer);

        _gitGutterRenderer = new GitGutterChangeRenderer();
        _editor.TextArea.TextView.BackgroundRenderers.Add(_gitGutterRenderer);

        _searchPanel = SearchPanel.Install(_editor.TextArea);

        _editor.TextArea.TextEntering += OnTextEntering;
        _editor.TextArea.TextEntered += OnTextEntered;
        _editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;

        ApplyEditorTheme();
    }

    private void ShowFindPanel()
    {
        if (_editor is null)
        {
            return;
        }

        if (ApplicationCommands.Find.CanExecute(null, _editor.TextArea))
        {
            ApplicationCommands.Find.Execute(null, _editor.TextArea);
            return;
        }

        _searchPanel?.Open();
    }

    private void ShowReplacePanel()
    {
        if (_editor is null)
        {
            return;
        }

        if (ApplicationCommands.Replace.CanExecute(null, _editor.TextArea))
        {
            ApplicationCommands.Replace.Execute(null, _editor.TextArea);
            return;
        }

        _searchPanel?.Open();
    }

    private void NewFile()
    {
        var dialog = new SaveFileDialog
        {
            Title = "New File",
            Filter = "C# Files (*.cs)|*.cs|All Files (*.*)|*.*|XAML Files (*.xaml)|*.xaml|XML Files (*.xml)|*.xml|JSON Files (*.json)|*.json|Text Files (*.txt)|*.txt",
            DefaultExt = ".cs",
            AddExtension = true,
            FileName = "NewFile.cs"
        };

        if (!string.IsNullOrWhiteSpace(ProjectRootPath) && Directory.Exists(ProjectRootPath))
        {
            dialog.InitialDirectory = ProjectRootPath;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            EditorService.WriteFile(dialog.FileName, string.Empty);
            OpenFileByPath(dialog.FileName);
            RefreshProjectItems();
        }
        catch (IOException ex)
        {
            MessageBox.Show($"Could not create file:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal IReadOnlyList<FileTemplate> GetExplorerFileTemplates()
    {
        return _templateService.GetTemplates();
    }

    internal void CreateFileFromTemplate(string templateName, ProjectItem? selectedItem)
    {
        if (string.IsNullOrWhiteSpace(ProjectRootPath) || !Directory.Exists(ProjectRootPath))
        {
            MessageBox.Show("Load a project or folder before creating a file from template.", "New File",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targetFolder = ResolveTemplateTargetFolder(selectedItem, ProjectRootPath);
        var suggestedFileName = $"New{SanitizeTemplateName(templateName)}.cs";
        var fileName = PromptForFileName(suggestedFileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
        {
            fileName += ".cs";
        }

        var fullPath = Path.Combine(targetFolder, fileName);
        if (File.Exists(fullPath))
        {
            MessageBox.Show($"File already exists:\n{fullPath}", "New File",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var content = _templateService.GenerateFromTemplate(templateName, fileName, targetFolder, ProjectRootPath);
            EditorService.WriteFile(fullPath, content);
            RefreshProjectItems();
            OpenFileByPath(fullPath);
        }
        catch (IOException ex)
        {
            MessageBox.Show($"Could not create file:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Template Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Template Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string ResolveTemplateTargetFolder(ProjectItem? selectedItem, string projectRootPath)
    {
        if (selectedItem is null)
        {
            return projectRootPath;
        }

        // Project/Solution nodes point to a file (.csproj/.sln); resolve to their directory
        if (selectedItem.ItemType is ProjectItemType.Project or ProjectItemType.Solution)
        {
            return Path.GetDirectoryName(selectedItem.FullPath) ?? projectRootPath;
        }

        if (selectedItem.IsDirectory)
        {
            return selectedItem.FullPath;
        }

        var directory = Path.GetDirectoryName(selectedItem.FullPath);
        return string.IsNullOrWhiteSpace(directory)
            ? projectRootPath
            : directory;
    }

    private static string SanitizeTemplateName(string templateName)
    {
        var chars = templateName
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();

        var normalized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(normalized)
            ? "File"
            : normalized;
    }

    private static string? PromptForFileName(string suggestedFileName)
    {
        var inputWindow = new System.Windows.Window
        {
            Title = "New File",
            Width = 380,
            Height = 150,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            Owner = System.Windows.Application.Current.MainWindow
        };

        string? result = null;
        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(12) };

        var label = new System.Windows.Controls.TextBlock
        {
            Text = "File name:",
            Margin = new System.Windows.Thickness(0, 0, 0, 6)
        };
        panel.Children.Add(label);

        var textBox = new System.Windows.Controls.TextBox
        {
            Text = suggestedFileName,
            Margin = new System.Windows.Thickness(0, 0, 0, 12)
        };
        textBox.SelectAll();
        panel.Children.Add(textBox);

        var buttonPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        var okButton = new System.Windows.Controls.Button
        {
            Content = "OK",
            Width = 75,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        okButton.Click += (_, _) =>
        {
            result = textBox.Text.Trim();
            inputWindow.DialogResult = true;
        };
        buttonPanel.Children.Add(okButton);

        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Width = 75,
            IsCancel = true
        };
        buttonPanel.Children.Add(cancelButton);

        panel.Children.Add(buttonPanel);
        inputWindow.Content = panel;

        textBox.Loaded += (_, _) => textBox.Focus();

        inputWindow.ShowDialog();
        return result;
    }

    private void ConfigureExplorerWatcher(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        DisposeExplorerWatcher();

        if (!Directory.Exists(rootPath))
        {
            return;
        }

        try
        {
            _explorerWatcher = new FileSystemWatcher(rootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
            };

            _explorerWatcher.Created += OnExplorerWatcherChanged;
            _explorerWatcher.Deleted += OnExplorerWatcherChanged;
            _explorerWatcher.Renamed += OnExplorerWatcherRenamed;
            _explorerWatcher.EnableRaisingEvents = true;
        }
        catch (IOException ex)
        {
            MessageBox.Show($"Could not watch folder:\n{ex.Message}", "File Explorer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show($"Access denied while watching folder:\n{ex.Message}", "File Explorer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DisposeExplorerWatcher()
    {
        if (_explorerWatcher is null)
        {
            return;
        }

        _explorerWatcher.EnableRaisingEvents = false;
        _explorerWatcher.Created -= OnExplorerWatcherChanged;
        _explorerWatcher.Deleted -= OnExplorerWatcherChanged;
        _explorerWatcher.Renamed -= OnExplorerWatcherRenamed;
        _explorerWatcher.Dispose();
        _explorerWatcher = null;
    }

    /// <summary>
    /// Configures a <see cref="FileSystemWatcher"/> that monitors project configuration files
    /// (<c>.csproj</c>, <c>Directory.Build.props</c>, <c>Directory.Packages.props</c>) for changes.
    /// When a matching file is modified on disk, the workspace is automatically re-evaluated.
    /// </summary>
    private void ConfigureProjectFileWatcher(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        DisposeProjectFileWatcher();

        if (!Directory.Exists(rootPath))
        {
            return;
        }

        try
        {
            _projectFileWatcher = new FileSystemWatcher(rootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
            };

            _projectFileWatcher.Changed += OnProjectFileWatcherChanged;
            _projectFileWatcher.Created += OnProjectFileWatcherChanged;
            _projectFileWatcher.Renamed += OnProjectFileWatcherRenamed;
            _projectFileWatcher.EnableRaisingEvents = true;
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"Could not watch project files: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"Access denied watching project files: {ex.Message}");
        }
    }

    private void DisposeProjectFileWatcher()
    {
        if (_projectFileWatcher is null)
        {
            return;
        }

        _projectFileWatcher.EnableRaisingEvents = false;
        _projectFileWatcher.Changed -= OnProjectFileWatcherChanged;
        _projectFileWatcher.Created -= OnProjectFileWatcherChanged;
        _projectFileWatcher.Renamed -= OnProjectFileWatcherRenamed;
        _projectFileWatcher.Dispose();
        _projectFileWatcher = null;
    }

    private void OnProjectFileWatcherChanged(object sender, FileSystemEventArgs e)
    {
        if (IsProjectConfigFile(e.FullPath))
        {
            QueueProjectFileReload();
        }
    }

    private void OnProjectFileWatcherRenamed(object sender, RenamedEventArgs e)
    {
        if (IsProjectConfigFile(e.FullPath) || IsProjectConfigFile(e.OldFullPath))
        {
            QueueProjectFileReload();
        }
    }

    private void QueueProjectFileReload()
    {
        _projectFileRefreshCts?.Cancel();
        _projectFileRefreshCts?.Dispose();
        _projectFileRefreshCts = new CancellationTokenSource();
        var ct = _projectFileRefreshCts.Token;

        _ = ReloadWorkspaceFromProjectFileChangeAsync(ct);
    }

    private async Task ReloadWorkspaceFromProjectFileChangeAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Debounce: project file saves may fire multiple events in rapid succession
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);

            var path = _loadedProjectOrSolutionPath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                var ext = Path.GetExtension(path);
                if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    await LoadSolutionFileAsync(path);
                }
                else if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    await LoadProjectFileAsync(path);
                }
            }, System.Windows.Threading.DispatcherPriority.Background, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer project file activity.
        }
    }

    /// <summary>
    /// Returns true if the given file path is a project configuration file that should
    /// trigger a workspace re-evaluation when changed on disk.
    /// </summary>
    internal static bool IsProjectConfigFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        string fileName = Path.GetFileName(filePath);
        return fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase);
    }

    private void OnExplorerWatcherChanged(object sender, FileSystemEventArgs e)
    {
        if (IsGitMetadataPath(e.FullPath))
        {
            return;
        }

        QueueExplorerRefresh();
    }

    private void OnExplorerWatcherRenamed(object sender, RenamedEventArgs e)
    {
        if (IsGitMetadataPath(e.FullPath) && IsGitMetadataPath(e.OldFullPath))
        {
            return;
        }

        QueueExplorerRefresh();
    }

    private void QueueExplorerRefresh()
    {
        _explorerRefreshCts?.Cancel();
        _explorerRefreshCts?.Dispose();
        _explorerRefreshCts = new CancellationTokenSource();
        var ct = _explorerRefreshCts.Token;

        _ = RefreshProjectItemsFromWatcherAsync(ct);
    }

    private async Task RefreshProjectItemsFromWatcherAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            await Application.Current.Dispatcher.InvokeAsync(
                RefreshProjectItems,
                System.Windows.Threading.DispatcherPriority.Background,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer filesystem activity.
        }
    }

    private static bool IsGitMetadataPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var marker = $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}";
        return path.Contains(marker, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith($"{Path.DirectorySeparatorChar}.git", StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshProjectItems()
    {
        if (string.IsNullOrWhiteSpace(ProjectRootPath) || !Directory.Exists(ProjectRootPath))
        {
            return;
        }

        // Collect expanded paths before rebuilding the tree
        var expandedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectExpandedPaths(ProjectItems, expandedPaths);

        // Rebuild tree based on what was originally loaded
        if (!string.IsNullOrEmpty(_loadedProjectOrSolutionPath))
        {
            var ext = Path.GetExtension(_loadedProjectOrSolutionPath);
            if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase))
            {
                // Re-discover project paths from the loaded solution result
                var projectPaths = DiscoverProjectPaths(_loadedProjectOrSolutionPath);
                var root = EditorService.BuildSolutionTree(_loadedProjectOrSolutionPath, projectPaths);
                RestoreExpandedPaths(root, expandedPaths);
                ProjectItems = new ObservableCollection<ProjectItem> { root };
                ApplyGitStatusToTree(_gitService.GetStatus());
                return;
            }

            if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var root = EditorService.BuildProjectTree(_loadedProjectOrSolutionPath);
                RestoreExpandedPaths(root, expandedPaths);
                ProjectItems = new ObservableCollection<ProjectItem> { root };
                ApplyGitStatusToTree(_gitService.GetStatus());
                return;
            }
        }

        // Fallback: folder-only view
        var folderRoot = EditorService.BuildFileTree(ProjectRootPath);
        RestoreExpandedPaths(folderRoot, expandedPaths);
        ProjectItems = new ObservableCollection<ProjectItem>(folderRoot.Children);
        ApplyGitStatusToTree(_gitService.GetStatus());
    }

    /// <summary>
    /// Discovers .csproj paths referenced in a .sln file by parsing Project lines.
    /// </summary>
    private static List<string> DiscoverProjectPaths(string solutionPath)
    {
        var solutionDir = Path.GetDirectoryName(solutionPath)!;
        var projectPaths = new List<string>();
        var lines = File.ReadAllLines(solutionPath);

        foreach (var line in lines)
        {
            if (!line.StartsWith("Project(", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split('"');
            if (parts.Length >= 6)
            {
                var relativePath = parts[5].Replace('\\', Path.DirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(solutionDir, relativePath));
                if (fullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    projectPaths.Add(fullPath);
                }
            }
        }

        return projectPaths;
    }

    private static void CollectExpandedPaths(IEnumerable<ProjectItem> items, HashSet<string> expandedPaths)
    {
        foreach (var item in items)
        {
            if (item.IsExpanded)
            {
                expandedPaths.Add(item.FullPath);
            }

            CollectExpandedPaths(item.Children, expandedPaths);
        }
    }

    private static void RestoreExpandedPaths(ProjectItem root, HashSet<string> expandedPaths)
    {
        if (expandedPaths.Contains(root.FullPath))
        {
            root.IsExpanded = true;
        }

        foreach (var child in root.Children)
        {
            RestoreExpandedPaths(child, expandedPaths);
        }
    }

    private void OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "All Files (*.*)|*.*|C# Files (*.cs)|*.cs|XML Files (*.xml;*.xaml;*.csproj)|*.xml;*.xaml;*.csproj|JSON Files (*.json)|*.json",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        OpenFileByPath(dialog.FileName);
    }

    private void OpenFolder()
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        LoadProjectRoot(dialog.FolderName);
    }

    private void OpenProject()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "C# Project Files (*.csproj)|*.csproj",
            Title = "Open C# Project"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _ = LoadProjectFileAsync(dialog.FileName);
    }

    private void OpenSolution()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Solution Files (*.sln)|*.sln",
            Title = "Open Solution"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _ = LoadSolutionFileAsync(dialog.FileName);
    }

    internal Task OpenProjectByPathAsync(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException("Project path is required.", nameof(projectPath));
        }

        return LoadProjectFileAsync(projectPath);
    }

    internal Task OpenSolutionByPathAsync(string solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            throw new ArgumentException("Solution path is required.", nameof(solutionPath));
        }

        return LoadSolutionFileAsync(solutionPath);
    }

    private async Task LoadProjectFileAsync(string projectPath)
    {
        if (!await CloseAllTabsAsync().ConfigureAwait(true))
        {
            return;
        }

        CancelPreviousLoad();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        var projectDir = Path.GetDirectoryName(projectPath)!;
        _isLoadingProject = true;
        LoadingStatus = $"Loading project: {Path.GetFileName(projectPath)}...";

        // Cancel any in-flight analysis before clearing the workspace
        _analysisCts?.Cancel();

        try
        {
            var ct = cts.Token;
            var result = await Task.Run(() =>
                MSBuildProjectLoader.LoadProjectAsync(projectPath, _roslynService, ct), ct).ConfigureAwait(true);

            ct.ThrowIfCancellationRequested();

            if (result is null)
            {
                MessageBox.Show($"Failed to load project:\n{projectPath}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            ProjectRootPath = projectDir;
            _loadedProjectOrSolutionPath = projectPath;
            var root = EditorService.BuildProjectTree(projectPath);
            ProjectItems = new ObservableCollection<ProjectItem> { root };
            ConfigureExplorerWatcher(projectDir);
            ConfigureProjectFileWatcher(projectDir);

            OpenRepositoryForPath(projectDir);

            LoadingStatus = $"Loaded: {result.Name} ({result.TargetFramework}) — {result.SourceFiles.Length} file(s)";
            ScheduleLoadingStatusClear();
        }
        catch (OperationCanceledException)
        {
            LoadingStatus = null;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load project:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            LoadingStatus = null;
        }
        finally
        {
            if (_loadCts == cts)
            {
                _isLoadingProject = false;
            }
        }
    }

    private async Task LoadSolutionFileAsync(string solutionPath)
    {
        if (!await CloseAllTabsAsync().ConfigureAwait(true))
        {
            return;
        }

        CancelPreviousLoad();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        var solutionDir = Path.GetDirectoryName(solutionPath)!;
        _isLoadingProject = true;
        LoadingStatus = $"Loading solution: {Path.GetFileName(solutionPath)}...";

        // Cancel any in-flight analysis before clearing the workspace
        _analysisCts?.Cancel();

        try
        {
            var ct = cts.Token;
            var result = await Task.Run(() =>
                MSBuildProjectLoader.LoadSolutionAsync(solutionPath, _roslynService, ct), ct).ConfigureAwait(true);

            ct.ThrowIfCancellationRequested();

            ProjectRootPath = solutionDir;
            _loadedProjectOrSolutionPath = solutionPath;
            var projectPaths = result.Projects.Select(p => p.ProjectPath).ToList();
            var root = EditorService.BuildSolutionTree(solutionPath, projectPaths);
            ProjectItems = new ObservableCollection<ProjectItem> { root };
            ConfigureExplorerWatcher(solutionDir);
            ConfigureProjectFileWatcher(solutionDir);

            OpenRepositoryForPath(solutionDir);

            var totalFiles = result.Projects.Sum(p => p.SourceFiles.Length);
            var projectNames = string.Join(", ", result.Projects.Select(p => p.Name));
            LoadingStatus = $"Loaded: {result.Name} — {result.Projects.Length} project(s), {totalFiles} file(s) [{projectNames}]";
            ScheduleLoadingStatusClear();
        }
        catch (OperationCanceledException)
        {
            LoadingStatus = null;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load solution:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            LoadingStatus = null;
        }
        finally
        {
            if (_loadCts == cts)
            {
                _isLoadingProject = false;
            }
        }
    }

    public void OpenFileByPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        var existing = OpenTabs.FirstOrDefault(t =>
            string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActivateTab(existing);
            return;
        }

        try
        {
            var content = EditorService.ReadFile(filePath);
            var highlighting = EditorService.GetSyntaxHighlighting(filePath);
            var tab = new OpenFileTab(filePath, content);
            OpenTabs.Add(tab);
            ActivateTab(tab, syntaxHighlighting: highlighting);
        }
        catch (IOException ex)
        {
            MessageBox.Show($"Could not open file:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Reloads a file from disk into any open editor tab and updates the Roslyn workspace.
    /// Called by agent tools after they write or edit a file so the editor and diagnostics
    /// stay in sync with the on-disk content.
    /// </summary>
    internal void NotifyFileChangedOnDisk(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (IOException)
        {
            return;
        }

        // Dispatch to the UI thread so we can safely update the AvalonEdit TextDocument
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var tab = OpenTabs.FirstOrDefault(t =>
                string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

            if (tab is not null)
            {
                // Temporarily suppress the text-changed handler to avoid marking
                // the file dirty or re-triggering analysis prematurely
                _isActivating = true;
                try
                {
                    tab.Document.Text = content;

                    // The file on disk matches the editor now, so it is not dirty
                    tab.IsDirty = false;
                }
                finally
                {
                    _isActivating = false;
                }
            }

            // Update the Roslyn workspace so diagnostics reflect the new content
            if (RoslynWorkspaceService.IsCSharpFile(filePath))
            {
                _ = _roslynService.OpenOrUpdateDocumentAsync(filePath, content);
                ScheduleRoslynAnalysis();
            }
        });
    }

    internal async Task<GitFileDiffResult?> GetFileDiffAsync(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            return await _gitService.GetFileDiffAsync(relativePath).ConfigureAwait(true);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Git Diff", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Git Diff", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        catch (IOException ex)
        {
            MessageBox.Show($"Failed to load file content for diff:\n{ex.Message}", "Git Diff",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        catch (LibGit2Sharp.LibGit2SharpException ex)
        {
            MessageBox.Show($"Failed to load diff:\n{ex.Message}", "Git Diff",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    public void LoadProjectRoot(string rootPath)
    {
        ArgumentNullException.ThrowIfNull(rootPath);

        ProjectRootPath = rootPath;
        _loadedProjectOrSolutionPath = null;
        var root = EditorService.BuildFileTree(rootPath);
        ProjectItems = new ObservableCollection<ProjectItem>(root.Children);
        ConfigureExplorerWatcher(rootPath);

        OpenRepositoryForPath(rootPath);
    }

    private void Save()
    {
        if (ActiveTab is null || _editor is null)
        {
            return;
        }

        if (ActiveTab.FilePath == "Untitled")
        {
            SaveAs();
            return;
        }

        try
        {
            EditorService.WriteFile(ActiveTab.FilePath, _editor.Text);
            ActiveTab.IsDirty = false;
            OnPropertyChanged(nameof(WindowTitle));
        }
        catch (IOException ex)
        {
            MessageBox.Show($"Could not save file:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveAs()
    {
        if (_editor is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "All Files (*.*)|*.*|C# Files (*.cs)|*.cs",
            FileName = ActiveTab?.FileName ?? "Untitled.cs"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var text = _editor.Text;
            EditorService.WriteFile(dialog.FileName, text);

            if (ActiveTab is not null)
            {
                OpenTabs.Remove(ActiveTab);
            }

            var newTab = new OpenFileTab(dialog.FileName, text);
            OpenTabs.Add(newTab);
            ActivateTab(newTab);
        }
        catch (IOException ex)
        {
            MessageBox.Show($"Could not save file:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CloseTab(OpenFileTab? tab)
    {
        tab ??= ActiveTab;
        if (tab is null)
        {
            return;
        }

        if (tab.IsDirty)
        {
            var result = MessageBox.Show(
                $"Save changes to {tab.FileName}?",
                "Unsaved Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            switch (result)
            {
                case MessageBoxResult.Yes:
                    Save();
                    break;
                case MessageBoxResult.Cancel:
                    return;
            }
        }

        OpenTabs.Remove(tab);
        await _roslynService.CloseDocumentAsync(tab.FilePath).ConfigureAwait(true);

        if (OpenTabs.Count > 0)
        {
            ActivateTab(OpenTabs[^1]);
        }
        else
        {
            ActiveTab = null;
            if (_editor is not null)
            {
                _editor.Document = new ICSharpCode.AvalonEdit.Document.TextDocument();
            }

            OnPropertyChanged(nameof(StatusText));
        }
    }

    private void OpenOptions()
    {
        var optionsWindow = new OptionsWindow
        {
            Owner = Application.Current.MainWindow
        };
        optionsWindow.ShowDialog();
    }

    private void ExitApplication()
    {
        foreach (var tab in OpenTabs.Where(t => t.IsDirty).ToList())
        {
            ActiveTab = tab;
            var result = MessageBox.Show(
                $"Save changes to {tab.FileName}?",
                "Unsaved Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            switch (result)
            {
                case MessageBoxResult.Yes:
                    Save();
                    break;
                case MessageBoxResult.Cancel:
                    return;
            }
        }

        Application.Current.Shutdown();
    }

    private void ActivateTab(OpenFileTab tab, string? syntaxHighlighting = null)
    {
        if (_editor is null)
        {
            return;
        }

        _isActivating = true;
        try
        {
            ActiveTab = tab;

            // Swap the underlying document — this preserves each tab's undo/redo history
            _editor.Document = tab.Document;

            var hlName = syntaxHighlighting ?? EditorService.GetSyntaxHighlighting(tab.FilePath);
            _editor.SyntaxHighlighting = hlName is not null
                ? HighlightingManager.Instance.GetDefinition(hlName)
                : null;
            EditorService.ApplySyntaxHighlightingTheme(_editor.SyntaxHighlighting);
            _editor.TextArea.TextView.Redraw();

            // Register with Roslyn for C# files
            if (RoslynWorkspaceService.IsCSharpFile(tab.FilePath))
            {
                // If the file is already tracked (from a loaded project), just update its text.
                // Otherwise, add it to the default adhoc project.
                var existingDoc = _roslynService.GetDocument(tab.FilePath);
                if (existingDoc is not null)
                {
                    _ = _roslynService.UpdateDocumentTextAsync(tab.FilePath, _editor.Text);
                }
                else
                {
                    _ = _roslynService.OpenOrUpdateDocumentAsync(tab.FilePath, _editor.Text);
                }

                if (_classificationColorizer is not null)
                {
                    _classificationColorizer.FilePath = tab.FilePath;
                }

                ScheduleRoslynAnalysis();
            }
            else
            {
                // Clear Roslyn overlays for non-C# files
                _diagnosticRenderer?.UpdateDiagnostics([]);
                if (_classificationColorizer is not null)
                {
                    _classificationColorizer.FilePath = null;
                }
            }

            UpdateGitGutterMarkers();

            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(StatusText));
        }
        finally
        {
            _isActivating = false;
        }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_isActivating || ActiveTab is null)
        {
            return;
        }

        if (!ActiveTab.IsDirty)
        {
            ActiveTab.IsDirty = true;
            OnPropertyChanged(nameof(WindowTitle));
        }

        // Clear loading status on first edit so it doesn't permanently override status text
        if (LoadingStatus is not null)
        {
            LoadingStatus = null;
        }

        UpdateGitGutterMarkers();
        ScheduleRoslynAnalysis();
    }

    public void SwitchToTab(OpenFileTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (_isActivating || tab == ActiveTab)
        {
            return;
        }

        ActivateTab(tab);
    }

    public void OnProjectItemSelected(ProjectItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Double-clicking a project/solution node toggles expansion
        if (item.ItemType is ProjectItemType.Project or ProjectItemType.Solution)
        {
            item.IsExpanded = !item.IsExpanded;
            return;
        }

        if (!item.IsDirectory)
        {
            OpenFileByPath(item.FullPath);
        }
    }

    /// <summary>
    /// Deletes a file or empty folder from disk and refreshes the explorer tree.
    /// Closes any open tab for a deleted file.
    /// </summary>
    internal void DeleteExplorerItem(ProjectItem? item)
    {
        if (item is null)
        {
            return;
        }

        // Don't allow deleting project/solution root nodes
        if (item.ItemType is ProjectItemType.Project or ProjectItemType.Solution)
        {
            MessageBox.Show("Cannot delete a project or solution node from the explorer.",
                "Delete", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var displayName = item.Name;
        var isDir = item.IsDirectory;
        var prompt = isDir
            ? $"Delete folder \"{displayName}\" and all its contents?"
            : $"Delete file \"{displayName}\"?";

        var result = MessageBox.Show(prompt, "Confirm Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (isDir)
            {
                // Close any tabs for files inside this directory
                var dirPath = item.FullPath + Path.DirectorySeparatorChar;
                var affectedTabs = OpenTabs
                    .Where(t => t.FilePath.StartsWith(dirPath, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var tab in affectedTabs)
                {
                    CloseTabCommand.Execute(tab);
                }

                Directory.Delete(item.FullPath, recursive: true);
            }
            else
            {
                // Close the tab if the file is open
                var tab = OpenTabs.FirstOrDefault(t =>
                    string.Equals(t.FilePath, item.FullPath, StringComparison.OrdinalIgnoreCase));

                if (tab is not null)
                {
                    CloseTabCommand.Execute(tab);
                }

                File.Delete(item.FullPath);
            }

            RefreshProjectItems();
        }
        catch (IOException ex)
        {
            MessageBox.Show($"Could not delete:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show($"Access denied:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Prompts the user for a new name and renames the file or folder on disk.
    /// Updates any open tabs and the Roslyn workspace for renamed .cs files.
    /// </summary>
    internal void RenameExplorerItem(ProjectItem? item)
    {
        if (item is null)
        {
            return;
        }

        // Don't allow renaming project/solution root nodes
        if (item.ItemType is ProjectItemType.Project or ProjectItemType.Solution)
        {
            MessageBox.Show("Cannot rename a project or solution node from the explorer.",
                "Rename", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var newName = PromptForInput("Rename", "New name:", item.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name)
        {
            return;
        }

        var parentDir = Path.GetDirectoryName(item.FullPath);
        if (string.IsNullOrEmpty(parentDir))
        {
            return;
        }

        var newPath = Path.Combine(parentDir, newName);
        if (File.Exists(newPath) || Directory.Exists(newPath))
        {
            MessageBox.Show($"An item named \"{newName}\" already exists.", "Rename",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (item.IsDirectory)
            {
                Directory.Move(item.FullPath, newPath);
            }
            else
            {
                File.Move(item.FullPath, newPath);

                // If the file is open in a tab, close and reopen at the new path
                var tab = OpenTabs.FirstOrDefault(t =>
                    string.Equals(t.FilePath, item.FullPath, StringComparison.OrdinalIgnoreCase));

                if (tab is not null)
                {
                    var wasDirty = tab.IsDirty;
                    var text = tab == ActiveTab && _editor is not null ? _editor.Text : tab.Document.Text;

                    OpenTabs.Remove(tab);
                    _ = _roslynService.CloseDocumentAsync(tab.FilePath);

                    var newTab = new OpenFileTab(newPath, text);
                    if (wasDirty)
                    {
                        newTab.IsDirty = true;
                    }

                    OpenTabs.Add(newTab);
                    ActivateTab(newTab);
                }
            }

            RefreshProjectItems();
        }
        catch (IOException ex)
        {
            MessageBox.Show($"Could not rename:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show($"Access denied:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Creates a new subfolder under the selected item (or project root) and refreshes the tree.
    /// </summary>
    internal void CreateNewFolder(ProjectItem? selectedItem)
    {
        if (string.IsNullOrWhiteSpace(ProjectRootPath) || !Directory.Exists(ProjectRootPath))
        {
            MessageBox.Show("Load a project or folder first.", "New Folder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targetFolder = ResolveTemplateTargetFolder(selectedItem, ProjectRootPath);

        var folderName = PromptForInput("New Folder", "Folder name:", "NewFolder");
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return;
        }

        var fullPath = Path.Combine(targetFolder, folderName);
        if (Directory.Exists(fullPath))
        {
            MessageBox.Show($"Folder already exists:\n{fullPath}", "New Folder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(fullPath);
            RefreshProjectItems();
        }
        catch (IOException ex)
        {
            MessageBox.Show($"Could not create folder:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Shows a simple input dialog with the given title, label, and default text.
    /// </summary>
    private static string? PromptForInput(string title, string labelText, string defaultText)
    {
        var inputWindow = new System.Windows.Window
        {
            Title = title,
            Width = 380,
            Height = 150,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            Owner = System.Windows.Application.Current.MainWindow
        };

        string? result = null;
        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(12) };

        var label = new System.Windows.Controls.TextBlock
        {
            Text = labelText,
            Margin = new System.Windows.Thickness(0, 0, 0, 6)
        };
        panel.Children.Add(label);

        var textBox = new System.Windows.Controls.TextBox
        {
            Text = defaultText,
            Margin = new System.Windows.Thickness(0, 0, 0, 12)
        };
        textBox.SelectAll();
        panel.Children.Add(textBox);

        var buttonPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        var okButton = new System.Windows.Controls.Button
        {
            Content = "OK",
            Width = 75,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        okButton.Click += (_, _) =>
        {
            result = textBox.Text.Trim();
            inputWindow.DialogResult = true;
        };
        buttonPanel.Children.Add(okButton);

        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Width = 75,
            IsCancel = true
        };
        buttonPanel.Children.Add(cancelButton);

        panel.Children.Add(buttonPanel);
        inputWindow.Content = panel;

        textBox.Loaded += (_, _) => textBox.Focus();

        inputWindow.ShowDialog();
        return result;
    }

    private void OnThemeChanged(AppTheme _)
    {
        ApplyEditorTheme();
    }

    private void ApplyEditorTheme()
    {
        if (_editor is null)
        {
            return;
        }

        if (Application.Current.TryFindResource(ThemeResourceKeys.EditorLineNumbersForeground) is Brush lineNumberBrush)
        {
            _editor.LineNumbersForeground = lineNumberBrush;
        }

        EditorService.ApplySyntaxHighlightingTheme(_editor.SyntaxHighlighting);
        _editor.TextArea.TextView.Redraw();
    }

    private void OnTextEntering(object? sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        if (_completionWindow is not null && e.Text.Length > 0)
        {
            var ch = e.Text[0];

            if (ch == '.')
            {
                // Close the current window without inserting so that OnTextEntered
                // can trigger a fresh member-access completion after the '.' is typed.
                _completionWindow.Close();
            }
            else if (RoslynCompletionProvider.IsCommitCharacter(ch))
            {
                _completionWindow.CompletionList.RequestInsertion(e);
            }
        }
    }

    private async void OnTextEntered(object? sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        if (ActiveTab is null || _editor is null)
        {
            return;
        }

        if (e.Text.Length != 1)
        {
            return;
        }

        var typedChar = e.Text[0];

        // Auto-trigger completion on '.' or identifier characters when no window is open
        if (_completionWindow is null && RoslynCompletionProvider.ShouldAutoTrigger(typedChar))
        {
            await ShowCompletionWindowAsync().ConfigureAwait(true);
        }

        // Trigger signature help on '(' or ','  
        if (typedChar is '(' or ',')
        {
            await ShowSignatureHelpAsync().ConfigureAwait(true);
        }
        // On ')' check if we're still inside an outer argument list (nested calls)
        else if (typedChar == ')' && _insightWindow is not null)
        {
            await UpdateSignatureHelpAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Handles caret position changes to update the active parameter highlight
    /// when the signature help window is open.
    /// </summary>
    private async void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_insightWindow is null)
        {
            return;
        }

        await UpdateSignatureHelpAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Shows the Roslyn completion window at the current caret position.
    /// Can be called from keyboard shortcut (Ctrl+Space) or auto-trigger.
    /// </summary>
    public async Task ShowCompletionWindowAsync()
    {
        if (ActiveTab is null || _editor is null || !RoslynWorkspaceService.IsCSharpFile(ActiveTab.FilePath))
        {
            return;
        }

        try
        {
            var caretOffset = _editor.CaretOffset;
            var currentText = _editor.Text;

            var result = await _completionProvider.GetCompletionsAsync(
                ActiveTab.FilePath, currentText, caretOffset).ConfigureAwait(true);

            if (result is null || result.Items.Count == 0)
            {
                return;
            }

            // Don't show if another window appeared while we were awaiting
            if (_completionWindow is not null)
            {
                return;
            }

            _completionWindow = new CompletionWindow(_editor.TextArea);

            // Set the start offset so AvalonEdit filters as the user types
            _completionWindow.StartOffset = result.SpanStart;

            RoslynCompletionProvider.ApplyTheme(_completionWindow);

            foreach (var item in result.Items)
            {
                _completionWindow.CompletionList.CompletionData.Add(item);
            }

            _completionWindow.Show();
            _completionWindow.Closed += (_, _) => _completionWindow = null;

            // The Roslyn query is async, so the user may have typed additional characters
            // between the trigger and now. AvalonEdit only filters on subsequent keystrokes,
            // so the initial display would be unfiltered. Pre-filter by extracting the text
            // already typed in the completion span and selecting into the list.
            var currentCaret = _editor.CaretOffset;
            if (currentCaret > _completionWindow.StartOffset)
            {
                var filterText = _editor.Document.GetText(
                    _completionWindow.StartOffset,
                    currentCaret - _completionWindow.StartOffset);
                _completionWindow.CompletionList.SelectItem(filterText);

                // If the filter text doesn't match anything, close immediately
                // so the user never sees an empty completion list.
                if (_completionWindow.CompletionList.SelectedItem is null)
                {
                    _completionWindow.Close();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ignored
        }
    }

    /// <summary>
    /// Shows the Roslyn signature help (parameter info) window at the current caret position.
    /// Triggered automatically when typing '(' or ','.
    /// </summary>
    public async Task ShowSignatureHelpAsync()
    {
        if (ActiveTab is null || _editor is null || !RoslynWorkspaceService.IsCSharpFile(ActiveTab.FilePath))
        {
            return;
        }

        _signatureHelpCts?.Cancel();
        _signatureHelpCts?.Dispose();
        _signatureHelpCts = new CancellationTokenSource();
        var ct = _signatureHelpCts.Token;

        try
        {
            var caretOffset = _editor.CaretOffset;
            await _roslynService.UpdateDocumentTextAsync(ActiveTab.FilePath, _editor.Text, ct).ConfigureAwait(true);

            var result = await _signatureHelpService.GetSignatureHelpAsync(
                ActiveTab.FilePath, caretOffset, ct).ConfigureAwait(true);

            if (result is null || result.Overloads.Count == 0)
            {
                return;
            }

            CloseSignatureHelp();

            _insightWindow = new OverloadInsightWindow(_editor.TextArea)
            {
                Provider = new SignatureHelpOverloadProvider(result)
            };

            if (Application.Current.TryFindResource(ThemeResourceKeys.TooltipBackground) is Brush sigBgBrush)
            {
                _insightWindow.Background = sigBgBrush;
            }

            _insightWindow.Closed += (_, _) => _insightWindow = null;
            _insightWindow.Show();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request
        }
    }

    /// <summary>
    /// Closes the signature help insight window if it is currently open.
    /// </summary>    
    private void CloseSignatureHelp()
    {
        if (_insightWindow is not null)
        {
            _insightWindow.Close();
            _insightWindow = null;
        }
    }

    /// <summary>
    /// Re-evaluates signature help at the current caret position.
    /// Updates the active parameter highlight and overload selection in an existing window,
    /// or closes the window if the caret has moved outside all argument lists.
    /// </summary>
    private async Task UpdateSignatureHelpAsync()
    {
        if (ActiveTab is null || _editor is null || _insightWindow is null
            || !RoslynWorkspaceService.IsCSharpFile(ActiveTab.FilePath))
        {
            return;
        }

        _signatureHelpCts?.Cancel();
        _signatureHelpCts?.Dispose();
        _signatureHelpCts = new CancellationTokenSource();
        var ct = _signatureHelpCts.Token;

        try
        {
            var caretOffset = _editor.CaretOffset;
            await _roslynService.UpdateDocumentTextAsync(ActiveTab.FilePath, _editor.Text, ct).ConfigureAwait(true);

            // Check if still inside an argument list
            bool insideArgs = await _signatureHelpService.IsInsideArgumentListAsync(
                ActiveTab.FilePath, caretOffset, ct).ConfigureAwait(true);

            if (!insideArgs)
            {
                CloseSignatureHelp();
                return;
            }

            var result = await _signatureHelpService.GetSignatureHelpAsync(
                ActiveTab.FilePath, caretOffset, ct).ConfigureAwait(true);

            if (result is null || result.Overloads.Count == 0)
            {
                CloseSignatureHelp();
                return;
            }

            // Update the existing provider in-place to avoid recreating the window
            if (_insightWindow?.Provider is SignatureHelpOverloadProvider provider)
            {
                provider.UpdateResult(result);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request
        }
    }

    /// <summary>
    /// Triggers background Roslyn analysis (classification + diagnostics) with a debounce delay.
    /// </summary>
    private void ScheduleRoslynAnalysis()
    {
        if (ActiveTab is null || !RoslynWorkspaceService.IsCSharpFile(ActiveTab.FilePath))
        {
            return;
        }

        _analysisCts?.Cancel();
        _analysisCts = new CancellationTokenSource();
        var ct = _analysisCts.Token;
        var filePath = ActiveTab.FilePath;
        var text = _editor?.Text ?? string.Empty;

        _ = RunRoslynAnalysisAsync(filePath, text, ct);
    }

    private async Task RunRoslynAnalysisAsync(string filePath, string text, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_analysisDelay, cancellationToken).ConfigureAwait(false);

            await _roslynService.UpdateDocumentTextAsync(filePath, text, cancellationToken).ConfigureAwait(false);

            // Semantic classification
            if (_classificationColorizer is not null)
            {
                _classificationColorizer.FilePath = filePath;
                await _classificationColorizer.UpdateClassificationsAsync(cancellationToken).ConfigureAwait(false);
            }

            // Diagnostics for all open C# documents, not just dependents of the active file
            var allDiagnosticItems = new List<DiagnosticItem>();
            var activeFileEntries = new List<DiagnosticEntry>();

            var filesToAnalyze = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Include all open tabs so the error list reflects the whole working set
            foreach (var tab in OpenTabs)
            {
                if (RoslynWorkspaceService.IsCSharpFile(tab.FilePath))
                {
                    filesToAnalyze.Add(tab.FilePath);
                }
            }

            // Also include tracked workspace documents that transitively depend on the edited file
            // so that cross-project errors are surfaced even for files not currently open as tabs
            foreach (var dep in _roslynService.GetDependentOpenDocumentFilePaths(filePath))
            {
                if (RoslynWorkspaceService.IsCSharpFile(dep))
                {
                    filesToAnalyze.Add(dep);
                }
            }

            foreach (var path in filesToAnalyze)
            {
                var (entries, items) = await BuildDiagnosticsForFileAsync(path, cancellationToken).ConfigureAwait(false);
                allDiagnosticItems.AddRange(items);

                if (string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    activeFileEntries = entries;
                }
            }

            // Update UI on dispatcher
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _diagnosticRenderer?.UpdateDiagnostics(activeFileEntries);
                _editor?.TextArea.TextView.Redraw();

                // Update error list panel
                DiagnosticItems.Clear();
                foreach (var item in allDiagnosticItems.OrderBy(i => i.Severity).ThenBy(i => i.File).ThenBy(i => i.Line).ThenBy(i => i.Column))
                {
                    DiagnosticItems.Add(item);
                }

                // Update status with diagnostic summary
                var errorCount = allDiagnosticItems.Count(e => e.Severity == DiagnosticSeverity.Error);
                var warningCount = allDiagnosticItems.Count(e => e.Severity == DiagnosticSeverity.Warning);
                if (errorCount > 0 || warningCount > 0)
                {
                    _diagnosticStatusText = $"{errorCount} error(s), {warningCount} warning(s)";
                }
                else
                {
                    _diagnosticStatusText = null;
                }

                OnPropertyChanged(nameof(StatusText));
            });
        }
        catch (OperationCanceledException)
        {
            // Analysis was superseded by a newer request
        }
    }

    private async Task<(List<DiagnosticEntry> Entries, List<DiagnosticItem> Items)> BuildDiagnosticsForFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var diagnostics = await _roslynService.GetDiagnosticsAsync(filePath, cancellationToken).ConfigureAwait(false);
        var entries = new List<DiagnosticEntry>();
        foreach (var diag in diagnostics)
        {
            if (diag.Severity == DiagnosticSeverity.Hidden)
            {
                continue;
            }

            var span = diag.Location.SourceSpan;
            entries.Add(new DiagnosticEntry(span.Start, span.End, diag.Severity, diag.GetMessage(), diag.Id));
        }

        var document = _roslynService.GetDocument(filePath);
        var sourceText = document is not null
            ? await document.GetTextAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var items = new List<DiagnosticItem>();
        var fileName = Path.GetFileName(filePath);
        foreach (var entry in entries)
        {
            var line = 0;
            var column = 0;
            if (sourceText is not null && entry.Start >= 0 && entry.Start <= sourceText.Length)
            {
                var linePosition = sourceText.Lines.GetLinePosition(entry.Start);
                line = linePosition.Line + 1;
                column = linePosition.Character + 1;
            }

            items.Add(new DiagnosticItem(
                entry.Severity, entry.Id, entry.Message,
                fileName, line, column,
                entry.Start, entry.End, filePath));
        }

        return (entries, items);
    }

    private string? _diagnosticStatusText;

    /// <summary>
    /// Navigates the editor to the source location of the specified diagnostic.
    /// Opens the file if it is not already open.
    /// </summary>
    public void NavigateToDiagnostic(DiagnosticItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (_editor is null)
        {
            return;
        }

        // Open the file if not already in a tab
        var tab = OpenTabs.FirstOrDefault(t =>
            string.Equals(t.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase));

        if (tab is null)
        {
            OpenFileByPath(item.FilePath);
            tab = ActiveTab;
        }
        else if (tab != ActiveTab)
        {
            ActivateTab(tab);
        }

        if (tab is null)
        {
            return;
        }

        // Navigate to offset
        var offset = Math.Min(item.StartOffset, _editor.Document.TextLength);
        if (offset >= 0)
        {
            _editor.CaretOffset = offset;
            _editor.ScrollToLine(item.Line);
            _editor.TextArea.Focus();
        }
    }

    /// <summary>
    /// Navigates the editor to the source location of a reference result.
    /// Opens the file if it is not already open.
    /// </summary>
    public void NavigateToReference(ReferenceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (_editor is null)
        {
            return;
        }

        var tab = OpenTabs.FirstOrDefault(t =>
            string.Equals(t.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase));

        if (tab is null)
        {
            OpenFileByPath(item.FilePath);
            tab = ActiveTab;
        }
        else if (tab != ActiveTab)
        {
            ActivateTab(tab);
        }

        if (tab is null)
        {
            return;
        }

        var offset = Math.Min(item.StartOffset, _editor.Document.TextLength);
        if (offset >= 0)
        {
            _editor.CaretOffset = offset;
            _editor.ScrollToLine(item.Line);
            _editor.TextArea.Focus();
        }
    }

    /// <summary>
    /// Opens a file (if not already open) and scrolls the editor to the specified line.
    /// Used by the presentation system to navigate between slides.
    /// </summary>
    public void NavigateToFileLine(string filePath, int line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (_editor is null)
        {
            return;
        }

        var tab = OpenTabs.FirstOrDefault(t =>
            string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        if (tab is null)
        {
            OpenFileByPath(filePath);
            tab = ActiveTab;
        }
        else if (tab != ActiveTab)
        {
            ActivateTab(tab);
        }

        if (tab is null)
        {
            return;
        }

        int clampedLine = Math.Max(1, Math.Min(line, _editor.Document.LineCount));
        var documentLine = _editor.Document.GetLineByNumber(clampedLine);
        _editor.CaretOffset = documentLine.Offset;
        _editor.ScrollToLine(clampedLine);
        _editor.TextArea.Focus();
    }

    /// <summary>
    /// Navigates to the source definition for the symbol at the caret (or provided offset).
    /// </summary>
    public async Task GoToDefinitionAsync(int? offset = null)
    {
        if (_editor is null || ActiveTab is null || !RoslynWorkspaceService.IsCSharpFile(ActiveTab.FilePath))
        {
            return;
        }

        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = new CancellationTokenSource();
        var ct = _navigationCts.Token;

        try
        {
            var targetOffset = offset ?? _editor.CaretOffset;
            await _roslynService.UpdateDocumentTextAsync(ActiveTab.FilePath, _editor.Text, ct).ConfigureAwait(true);

            var target = await _navigationService
                .FindDefinitionAsync(ActiveTab.FilePath, targetOffset, ct)
                .ConfigureAwait(true);

            if (target is null)
            {
                return;
            }

            // Open the target file if needed, then move caret to definition location.
            var tab = OpenTabs.FirstOrDefault(t =>
                string.Equals(t.FilePath, target.FilePath, StringComparison.OrdinalIgnoreCase));

            if (tab is null)
            {
                OpenFileByPath(target.FilePath);
                tab = ActiveTab;
            }
            else if (tab != ActiveTab)
            {
                ActivateTab(tab);
            }

            if (tab is null)
            {
                return;
            }

            var clampedOffset = Math.Clamp(target.Offset, 0, _editor.Document.TextLength);
            _editor.CaretOffset = clampedOffset;

            var line = _editor.Document.GetLineByOffset(clampedOffset);
            _editor.ScrollToLine(line.LineNumber);
            _editor.TextArea.Focus();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer navigation request.
        }
    }

    private bool CanGoToDefinition()
    {
        return _editor is not null
            && ActiveTab is not null
            && RoslynWorkspaceService.IsCSharpFile(ActiveTab.FilePath);
    }

    /// <summary>
    /// Navigates to symbol implementations for the symbol at the caret.
    /// When multiple implementations are found, populates the Find References panel.
    /// </summary>
    public async Task GoToImplementationAsync()
    {
        if (_editor is null || ActiveTab is null || !RoslynWorkspaceService.IsCSharpFile(ActiveTab.FilePath))
        {
            return;
        }

        var filePath = ActiveTab.FilePath;
        var caretOffset = _editor.CaretOffset;

        await NavigateToRelatedSymbolsAsync(
            "Searching implementations...",
            "implementation(s)",
            "No implementations found",
            ct => _navigationService.FindImplementationsAsync(filePath, caretOffset, ct))
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Navigates to derived classes for the type symbol at the caret.
    /// When multiple derived classes are found, populates the Find References panel.
    /// </summary>
    public async Task GoToDerivedTypesAsync()
    {
        if (_editor is null || ActiveTab is null || !RoslynWorkspaceService.IsCSharpFile(ActiveTab.FilePath))
        {
            return;
        }

        var filePath = ActiveTab.FilePath;
        var caretOffset = _editor.CaretOffset;

        await NavigateToRelatedSymbolsAsync(
            "Searching derived types...",
            "derived type(s)",
            "No derived types found",
            ct => _navigationService.FindDerivedTypesAsync(filePath, caretOffset, ct))
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Finds all references to the symbol at the caret across the solution and populates the Find References panel.
    /// </summary>
    public async Task FindReferencesAsync()
    {
        if (_editor is null || ActiveTab is null || !RoslynWorkspaceService.IsCSharpFile(ActiveTab.FilePath))
        {
            return;
        }

        _findReferencesCts?.Cancel();
        _findReferencesCts?.Dispose();
        _findReferencesCts = new CancellationTokenSource();
        var ct = _findReferencesCts.Token;

        FindReferencesStatusText = "Searching...";
        ReferenceItems.Clear();

        try
        {
            var caretOffset = _editor.CaretOffset;
            await _roslynService.UpdateDocumentTextAsync(ActiveTab.FilePath, _editor.Text, ct).ConfigureAwait(true);

            var results = await _navigationService
                .FindReferencesAsync(ActiveTab.FilePath, caretOffset, ct)
                .ConfigureAwait(true);

            ReferenceItems.Clear();
            foreach (var item in results)
            {
                ReferenceItems.Add(item);
            }

            var symbolName = results.Count > 0 ? results[0].SymbolName : string.Empty;
            FindReferencesStatusText = results.Count > 0
                ? $"{symbolName} — {results.Count} reference(s)"
                : "No references found";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request
        }
    }

    private async Task NavigateToRelatedSymbolsAsync(
        string searchingText,
        string successSuffix,
        string notFoundText,
        Func<CancellationToken, Task<IReadOnlyList<ReferenceItem>>> searchAsync)
    {
        if (_editor is null || ActiveTab is null || !RoslynWorkspaceService.IsCSharpFile(ActiveTab.FilePath))
        {
            return;
        }

        _findReferencesCts?.Cancel();
        _findReferencesCts?.Dispose();
        _findReferencesCts = new CancellationTokenSource();
        var ct = _findReferencesCts.Token;

        FindReferencesStatusText = searchingText;
        ReferenceItems.Clear();

        try
        {
            await _roslynService.UpdateDocumentTextAsync(ActiveTab.FilePath, _editor.Text, ct).ConfigureAwait(true);

            var results = await searchAsync(ct).ConfigureAwait(true);
            if (results.Count == 0)
            {
                FindReferencesStatusText = notFoundText;
                return;
            }

            foreach (var item in results)
            {
                ReferenceItems.Add(item);
            }

            var symbolName = results[0].SymbolName;
            FindReferencesStatusText = $"{symbolName} — {results.Count} {successSuffix}";

            if (results.Count == 1)
            {
                NavigateToReference(results[0]);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request
        }
    }

    /// <summary>
    /// Gets available code actions (fixes + refactorings) at the current caret position.
    /// The returned items are displayed in the CodeActionLightBulb popup.
    /// </summary>
    public async Task ShowCodeActionsAsync()
    {
        if (_editor is null || ActiveTab is null || !RoslynWorkspaceService.IsCSharpFile(ActiveTab.FilePath))
        {
            return;
        }

        _codeActionsCts?.Cancel();
        _codeActionsCts?.Dispose();
        _codeActionsCts = new CancellationTokenSource();
        var ct = _codeActionsCts.Token;

        try
        {
            var caretOffset = _editor.CaretOffset;
            await _roslynService.UpdateDocumentTextAsync(ActiveTab.FilePath, _editor.Text, ct).ConfigureAwait(true);

            var items = await _codeActionService
                .GetCodeActionsAsync(ActiveTab.FilePath, caretOffset, ct)
                .ConfigureAwait(true);

            if (items.Count == 0)
            {
                return;
            }

            CodeActionsReady?.Invoke(items);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request
        }
    }

    /// <summary>
    /// Triggers Roslyn member generation actions at the current caret position.
    /// Applies a single matching action automatically, or shows choices when multiple are available.
    /// </summary>
    public async Task GenerateMissingMembersAsync()
    {
        if (_editor is null || ActiveTab is null || !RoslynWorkspaceService.IsCSharpFile(ActiveTab.FilePath))
        {
            return;
        }

        _codeActionsCts?.Cancel();
        _codeActionsCts?.Dispose();
        _codeActionsCts = new CancellationTokenSource();
        var ct = _codeActionsCts.Token;

        try
        {
            var caretOffset = _editor.CaretOffset;
            await _roslynService.UpdateDocumentTextAsync(ActiveTab.FilePath, _editor.Text, ct).ConfigureAwait(true);

            var items = await _codeActionService
                .GetGenerateMissingMembersActionsAsync(ActiveTab.FilePath, caretOffset, ct)
                .ConfigureAwait(true);

            if (items.Count == 0)
            {
                return;
            }

            if (items.Count == 1)
            {
                await ApplyCodeActionAsync(items[0]).ConfigureAwait(true);
                return;
            }

            CodeActionsReady?.Invoke(items);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request
        }
    }

    /// <summary>
    /// Applies a selected code action and updates the editor text.
    /// For multi-file code actions, updates all affected open tabs and writes
    /// changes to files not currently open.
    /// </summary>
    public async Task ApplyCodeActionAsync(Models.CodeActionItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (_editor is null || ActiveTab is null)
        {
            return;
        }

        try
        {
            var multiResult = await _codeActionService
                .ApplyCodeActionMultiFileAsync(ActiveTab.FilePath, item.Action)
                .ConfigureAwait(true);

            if (multiResult is null)
            {
                return;
            }

            ApplySolutionEdits(multiResult);
        }
        catch (OperationCanceledException)
        {
            // Ignored
        }
    }

    /// <summary>
    /// Raised when code actions are ready to be displayed.
    /// The MainWindow subscribes to show the lightbulb popup.
    /// </summary>
    public event Action<IReadOnlyList<Models.CodeActionItem>>? CodeActionsReady;

    /// <summary>
    /// Prompts the user for a new name and renames the symbol at the caret across the solution.
    /// </summary>
    public async Task RenameSymbolAsync()
    {
        if (_editor is null || ActiveTab is null || !RoslynWorkspaceService.IsCSharpFile(ActiveTab.FilePath))
        {
            return;
        }

        _renameCts?.Cancel();
        _renameCts?.Dispose();
        _renameCts = new CancellationTokenSource();
        var ct = _renameCts.Token;

        try
        {
            var caretOffset = _editor.CaretOffset;
            await _roslynService.UpdateDocumentTextAsync(ActiveTab.FilePath, _editor.Text, ct).ConfigureAwait(true);

            var symbolInfo = await _refactoringService
                .GetSymbolNameAtPositionAsync(ActiveTab.FilePath, caretOffset, ct)
                .ConfigureAwait(true);

            if (symbolInfo is null)
            {
                return;
            }

            var newName = PromptForNewName(symbolInfo.Name);
            if (string.IsNullOrWhiteSpace(newName) || newName == symbolInfo.Name)
            {
                return;
            }

            var result = await _refactoringService
                .RenameSymbolAsync(ActiveTab.FilePath, caretOffset, newName, ct)
                .ConfigureAwait(true);

            if (result is null)
            {
                return;
            }

            ApplySolutionEdits(result);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request
        }
    }

    /// <summary>
    /// Triggers the Extract Method refactoring on the current text selection.
    /// This works by finding the "Extract method" code action and applying it.
    /// </summary>
    public async Task ExtractMethodAsync()
    {
        if (_editor is null || ActiveTab is null || !RoslynWorkspaceService.IsCSharpFile(ActiveTab.FilePath))
        {
            return;
        }

        _codeActionsCts?.Cancel();
        _codeActionsCts?.Dispose();
        _codeActionsCts = new CancellationTokenSource();
        var ct = _codeActionsCts.Token;

        try
        {
            var selection = _editor.TextArea.Selection;
            if (selection.IsEmpty)
            {
                return;
            }

            var startOffset = _editor.Document.GetOffset(selection.StartPosition.Location);
            var endOffset = _editor.Document.GetOffset(selection.EndPosition.Location);
            var midpoint = (startOffset + endOffset) / 2;

            await _roslynService.UpdateDocumentTextAsync(ActiveTab.FilePath, _editor.Text, ct).ConfigureAwait(true);

            var items = await _codeActionService
                .GetCodeActionsAsync(ActiveTab.FilePath, midpoint, ct)
                .ConfigureAwait(true);

            // Find the extract method action
            var extractAction = items.FirstOrDefault(a =>
                a.Title.Contains("Extract method", StringComparison.OrdinalIgnoreCase)
                || a.Title.Contains("Extract local function", StringComparison.OrdinalIgnoreCase));

            if (extractAction is null)
            {
                // Fall back to showing all available actions so the user can pick
                if (items.Count > 0)
                {
                    CodeActionsReady?.Invoke(items);
                }
                return;
            }

            await ApplyCodeActionAsync(extractAction);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request
        }
    }

    /// <summary>
    /// Applies a full solution edit: updates changed files in open tabs or on disk,
    /// writes newly added files to disk, removes deleted files from the workspace,
    /// and keeps Roslyn workspace state in sync.
    /// </summary>
    private void ApplySolutionEdits(Models.SolutionEditResult editResult)
    {
        Debug.WriteLine($"[ApplySolutionEdits] Applying edits: {editResult.ChangedFiles.Count} changed, {editResult.AddedFiles.Count} added, {editResult.RemovedFiles.Count} removed");

        List<string>? writeFailures = null;

        // 1. Changed documents
        foreach (var (filePath, newText) in editResult.ChangedFiles)
        {
            OpenFileTab? tab = OpenTabs.FirstOrDefault(t =>
                string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

            if (tab is not null && tab == ActiveTab && _editor is not null)
            {
                _editor.Text = newText;
            }
            else if (tab is not null)
            {
                tab.Document.Text = newText;
                tab.IsDirty = true;
            }
            else
            {
                if (!TryWriteFile(filePath, newText))
                {
                    writeFailures ??= [];
                    writeFailures.Add(filePath);
                }
            }

            _ = _roslynService.UpdateDocumentTextAsync(filePath, newText);
        }

        // 2. Added documents
        foreach (var (filePath, newText) in editResult.AddedFiles)
        {
            if (!TryWriteFile(filePath, newText))
            {
                writeFailures ??= [];
                writeFailures.Add(filePath);
            }

            _ = _roslynService.OpenOrUpdateDocumentAsync(filePath, newText);
        }

        // 3. Removed documents
        foreach (string filePath in editResult.RemovedFiles)
        {
            // Close the tab if it is open
            OpenFileTab? tab = OpenTabs.FirstOrDefault(t =>
                string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

            if (tab is not null)
            {
                OpenTabs.Remove(tab);
                if (tab == ActiveTab)
                {
                    ActiveTab = OpenTabs.FirstOrDefault();
                }
            }

            _ = _roslynService.CloseDocumentAsync(filePath);

            // Delete the file from disk if it exists
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"[ApplySolutionEdits] Failed to delete file '{filePath}': {ex.Message}");
                writeFailures ??= [];
                writeFailures.Add(filePath);
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"[ApplySolutionEdits] Access denied deleting file '{filePath}': {ex.Message}");
                writeFailures ??= [];
                writeFailures.Add(filePath);
            }
        }

        if (writeFailures is not null)
        {
            string fileList = string.Join("\n", writeFailures);
            MessageBox.Show(
                $"Could not write/delete the following files:\n{fileList}",
                "Multi-File Edit Warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        ScheduleRoslynAnalysis();
    }

    /// <summary>
    /// Attempts to write content to a file, returning false on I/O or access errors.
    /// </summary>
    private static bool TryWriteFile(string filePath, string content)
    {
        try
        {
            EditorService.WriteFile(filePath, content);
            return true;
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"[ApplySolutionEdits] Failed to write file '{filePath}': {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"[ApplySolutionEdits] Access denied writing file '{filePath}': {ex.Message}");
            return false;
        }
    }

    private static string? PromptForNewName(string currentName)
    {
        var inputWindow = new System.Windows.Window
        {
            Title = "Rename Symbol",
            Width = 350,
            Height = 150,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            Owner = System.Windows.Application.Current.MainWindow
        };

        string? result = null;
        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(12) };

        var label = new System.Windows.Controls.TextBlock
        {
            Text = "New name:",
            Margin = new System.Windows.Thickness(0, 0, 0, 6)
        };
        panel.Children.Add(label);

        var textBox = new System.Windows.Controls.TextBox
        {
            Text = currentName,
            Margin = new System.Windows.Thickness(0, 0, 0, 12)
        };
        textBox.SelectAll();
        panel.Children.Add(textBox);

        var buttonPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        var okButton = new System.Windows.Controls.Button
        {
            Content = "OK",
            Width = 75,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        okButton.Click += (_, _) =>
        {
            result = textBox.Text;
            inputWindow.DialogResult = true;
        };
        buttonPanel.Children.Add(okButton);

        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Width = 75,
            IsCancel = true
        };
        buttonPanel.Children.Add(cancelButton);

        panel.Children.Add(buttonPanel);
        inputWindow.Content = panel;

        textBox.Loaded += (_, _) => textBox.Focus();

        inputWindow.ShowDialog();
        return result;
    }

    private bool CanExtractMethod()
    {
        return _editor is not null
            && ActiveTab is not null
            && RoslynWorkspaceService.IsCSharpFile(ActiveTab.FilePath)
            && _editor.TextArea.Selection is { IsEmpty: false };
    }

    private void OpenRepositoryForPath(string path)
    {
        _gitService.TryOpenRepository(path);
        ApplyGitStatusToTree(_gitService.GetStatus());
        UpdateGitRepositoryState();
    }

    private async Task CommitChangesAsync()
    {
        var message = PromptForInput("Git Commit", "Commit message:", string.Empty);
        if (message is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            MessageBox.Show("Commit message is required.", "Git Commit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await _gitService.CommitAsync(message.Trim()).ConfigureAwait(true);
            RefreshGitStatus();
            MessageBox.Show("Commit created.", "Git Commit",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Git Commit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Git Commit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (LibGit2Sharp.LibGit2SharpException ex)
        {
            MessageBox.Show($"Commit failed:\n{ex.Message}", "Git Commit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshGitStatus()
    {
        _gitService.RefreshStatus();
        UpdateGitRepositoryState();
    }

    private void InitializeGitRepository()
    {
        if (string.IsNullOrWhiteSpace(ProjectRootPath) || !Directory.Exists(ProjectRootPath))
        {
            return;
        }

        if (!_gitService.TryInitializeRepository(ProjectRootPath))
        {
            MessageBox.Show($"Failed to initialize Git repository:\n{ProjectRootPath}", "Git",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApplyGitStatusToTree(_gitService.GetStatus());
        UpdateGitRepositoryState();
    }

    private void DiscardFile(GitChangesEntry? entry)
    {
        if (entry is null) return;

        var result = MessageBox.Show(
            $"Discard all changes to \"{entry.RelativePath}\"?\nThis cannot be undone.",
            "Discard Changes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        ExecuteGitOperation(() => _gitService.DiscardFile(entry.RelativePath));
    }

    private void ResolveConflict(GitChangesEntry? entry, GitConflictResolution resolution)
    {
        if (entry is null)
        {
            return;
        }

        ExecuteGitOperation(() => _gitService.ResolveConflict(entry.RelativePath, resolution));

        if (_editor is not null && ActiveTab is not null
            && string.Equals(ActiveTab.FilePath, entry.FullPath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(entry.FullPath))
        {
            var updatedText = EditorService.ReadFile(entry.FullPath);
            _editor.Text = updatedText;
            ActiveTab.Document.Text = updatedText;
        }

        UpdateGitGutterMarkers();
    }

    private void ExecuteGitOperation(Action operation)
    {
        try
        {
            operation();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Git Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Git Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (LibGit2Sharp.LibGit2SharpException ex)
        {
            MessageBox.Show($"Git operation failed:\n{ex.Message}", "Git Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string AsRelativePath(object? param) =>
        (param as GitChangesEntry)?.RelativePath ?? string.Empty;

    private void UpdateGitGutterMarkers()
    {
        if (_gitGutterRenderer is null || _editor is null || ActiveTab is null)
        {
            return;
        }

        if (!_gitService.IsRepositoryOpen || !_gitService.TryGetRelativePath(ActiveTab.FilePath, out var relativePath) || string.IsNullOrWhiteSpace(relativePath))
        {
            _gitGutterRenderer.UpdateChanges([]);
            _editor.TextArea.TextView.Redraw();
            return;
        }

        var headText = _gitService.GetHeadFileText(relativePath);
        var currentText = _editor.Text;

        var changes = BuildGitLineChanges(headText, currentText, _editor.Document.LineCount);
        _gitGutterRenderer.UpdateChanges(changes);
        _editor.TextArea.TextView.Redraw();
    }

    private static IReadOnlyList<GitLineChange> BuildGitLineChanges(string headText, string currentText, int currentLineCount)
    {
        var headLines = headText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var currentLines = currentText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        var changes = new List<GitLineChange>();
        var maxLines = Math.Max(headLines.Length, currentLines.Length);

        for (var index = 0; index < maxLines; index++)
        {
            var hasHead = index < headLines.Length;
            var hasCurrent = index < currentLines.Length;

            if (!hasHead && hasCurrent)
            {
                changes.Add(new GitLineChange(index + 1, GitLineChangeType.Added));
                continue;
            }

            if (hasHead && !hasCurrent)
            {
                var markerLine = Math.Min(index + 1, Math.Max(currentLineCount, 1));
                changes.Add(new GitLineChange(markerLine, GitLineChangeType.Deleted));
                continue;
            }

            if (!string.Equals(headLines[index], currentLines[index], StringComparison.Ordinal))
            {
                changes.Add(new GitLineChange(index + 1, GitLineChangeType.Modified));
            }
        }

        return changes;
    }

    private async Task CheckoutSelectedBranchAsync(string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName) || !_gitService.IsRepositoryOpen)
        {
            return;
        }

        if (string.Equals(branchName, _gitService.CurrentBranchName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await _gitService.CheckoutAsync(branchName).ConfigureAwait(true);
            RefreshProjectItems();
            RefreshGitStatus();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Git Branch", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshGitBranches();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Git Branch", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshGitBranches();
        }
        catch (LibGit2Sharp.LibGit2SharpException ex)
        {
            MessageBox.Show($"Branch checkout failed:\n{ex.Message}", "Git Branch",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshGitBranches();
        }
    }

    private async Task CreateBranchAsync()
    {
        var branchName = PromptForInput("Create Branch", "Branch name:", string.Empty);
        if (branchName is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(branchName))
        {
            MessageBox.Show("Branch name is required.", "Git Branch",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await _gitService.CreateBranchAsync(branchName.Trim()).ConfigureAwait(true);
            RefreshGitBranches();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Git Branch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Git Branch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (LibGit2Sharp.LibGit2SharpException ex)
        {
            MessageBox.Show($"Branch creation failed:\n{ex.Message}", "Git Branch",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task DeleteBranchAsync()
    {
        var defaultBranchName = SelectedGitBranch ?? string.Empty;
        var branchName = PromptForInput("Delete Branch", "Branch name:", defaultBranchName);
        if (branchName is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(branchName))
        {
            MessageBox.Show("Branch name is required.", "Git Branch",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var trimmedName = branchName.Trim();
        var result = MessageBox.Show(
            $"Delete branch '{trimmedName}'?",
            "Delete Branch",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _gitService.DeleteBranchAsync(trimmedName).ConfigureAwait(true);
            RefreshGitBranches();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Git Branch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Git Branch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (LibGit2Sharp.LibGit2SharpException ex)
        {
            MessageBox.Show($"Branch deletion failed:\n{ex.Message}", "Git Branch",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private Task FetchAsync()
    {
        return RunRemoteOperationAsync("Fetch", progress => _gitService.FetchAsync(progress: progress));
    }

    private Task PullAsync()
    {
        return RunRemoteOperationAsync("Pull", progress => _gitService.PullAsync(progress: progress));
    }

    private Task PushAsync()
    {
        return RunRemoteOperationAsync("Push", progress => _gitService.PushAsync(progress: progress));
    }

    private async Task RunRemoteOperationAsync(string operationName, Func<IProgress<string>, Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(operation);

        BuildOutputLines.Clear();
        BuildSummary = $"Git {operationName}...";
        BuildOutputLines.Add($"> git {operationName.ToLowerInvariant()}");
        BuildOutputLines.Add(string.Empty);

        var progress = new Progress<string>(line => BuildOutputLines.Add(line));

        try
        {
            await operation(progress).ConfigureAwait(true);
            BuildSummary = $"Git {operationName} succeeded";
            BuildOutputLines.Add(string.Empty);
            BuildOutputLines.Add($"Git {operationName} completed successfully.");

            RefreshGitStatus();
            UpdateGitRepositoryState();
        }
        catch (OperationCanceledException)
        {
            BuildSummary = $"Git {operationName} cancelled";
            BuildOutputLines.Add("Operation cancelled.");
        }
        catch (ArgumentException ex)
        {
            BuildSummary = $"Git {operationName} failed";
            BuildOutputLines.Add($"Error: {ex.Message}");
            MessageBox.Show(ex.Message, $"Git {operationName}", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (InvalidOperationException ex)
        {
            BuildSummary = $"Git {operationName} failed";
            BuildOutputLines.Add($"Error: {ex.Message}");
            MessageBox.Show(ex.Message, $"Git {operationName}", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (LibGit2Sharp.LibGit2SharpException ex)
        {
            BuildSummary = $"Git {operationName} failed";
            BuildOutputLines.Add($"Error: {ex.Message}");
            MessageBox.Show($"Git {operationName} failed:\n{ex.Message}",
                $"Git {operationName}", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshGitBranches()
    {
        _isUpdatingSelectedBranch = true;
        try
        {
            GitBranches.Clear();
            foreach (var branch in _gitService.GetLocalBranches())
            {
                GitBranches.Add(branch);
            }

            SelectedGitBranch = _gitService.CurrentBranchName;
        }
        finally
        {
            _isUpdatingSelectedBranch = false;
        }
    }

    private void RefreshGitLog()
    {
        GitCommitHistory.Clear();

        var commits = _gitService.GetCommitHistory();
        foreach (var commit in commits)
        {
            GitCommitHistory.Add(commit);
        }

        GitLogStatusText = commits.Count > 0
            ? $"{commits.Count} commit(s)"
            : "No commits";
    }

    private void UpdateGitRepositoryState()
    {
        RefreshGitBranches();
        RefreshGitLog();
        OnPropertyChanged(nameof(CanInitializeGitRepository));
        OnPropertyChanged(nameof(GitRepositoryStatusText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnGitStatusChanged(IReadOnlyList<GitFileStatusEntry> entries)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            ApplyGitStatusToTree(entries);
            UpdateGitGutterMarkers();
            UpdateGitRepositoryState();
        });
    }

    private void ApplyGitStatusToTree(IReadOnlyList<GitFileStatusEntry> entries)
    {
        var workDir = _gitService.RepositoryWorkingDirectory;
        if (workDir is null)
        {
            ClearGitBadgesOnItems(ProjectItems);
            ClearGitChangesCollections();
            return;
        }

        var statusByPath = new Dictionary<string, GitStatusBadge>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            // LibGit2Sharp uses forward-slash separators; normalize to the OS separator.
            var normalized = entry.FilePath.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(workDir, normalized));
            statusByPath[fullPath] = ToGitStatusBadge(entry.Status);
        }

        foreach (var item in ProjectItems)
        {
            ApplyBadgeToItem(item, statusByPath);
        }

        UpdateGitChangesCollections(entries, workDir);
    }

    private static GitStatusBadge ApplyBadgeToItem(
        ProjectItem item,
        Dictionary<string, GitStatusBadge> statusByPath)
    {
        if (!item.IsDirectory)
        {
            var badge = statusByPath.TryGetValue(item.FullPath, out var b) ? b : GitStatusBadge.None;
            item.GitBadge = badge;
            return badge;
        }

        var worst = GitStatusBadge.None;
        foreach (var child in item.Children)
        {
            var childBadge = ApplyBadgeToItem(child, statusByPath);
            if (BadgeSeverity(childBadge) > BadgeSeverity(worst))
            {
                worst = childBadge;
            }
        }

        item.GitBadge = worst;
        return worst;
    }

    private static void ClearGitBadgesOnItems(IEnumerable<ProjectItem> items)
    {
        foreach (var item in items)
        {
            item.GitBadge = GitStatusBadge.None;
            if (item.IsDirectory)
            {
                ClearGitBadgesOnItems(item.Children);
            }
        }
    }

    private static GitStatusBadge ToGitStatusBadge(LibGit2Sharp.FileStatus status)
    {
        if (status.HasFlag(LibGit2Sharp.FileStatus.Conflicted))         return GitStatusBadge.Conflict;
        if (status.HasFlag(LibGit2Sharp.FileStatus.DeletedFromWorkdir)
            || status.HasFlag(LibGit2Sharp.FileStatus.DeletedFromIndex))  return GitStatusBadge.Deleted;
        if (status.HasFlag(LibGit2Sharp.FileStatus.ModifiedInWorkdir)
            || status.HasFlag(LibGit2Sharp.FileStatus.ModifiedInIndex))   return GitStatusBadge.Modified;
        if (status.HasFlag(LibGit2Sharp.FileStatus.NewInIndex))           return GitStatusBadge.Added;
        if (status.HasFlag(LibGit2Sharp.FileStatus.NewInWorkdir))         return GitStatusBadge.Untracked;
        return GitStatusBadge.None;
    }

    private static int BadgeSeverity(GitStatusBadge badge) => badge switch
    {
        GitStatusBadge.Conflict  => 5,
        GitStatusBadge.Deleted   => 4,
        GitStatusBadge.Modified  => 3,
        GitStatusBadge.Added     => 2,
        GitStatusBadge.Untracked => 1,
        _                        => 0
    };

    private void UpdateGitChangesCollections(IReadOnlyList<GitFileStatusEntry> entries, string workDir)
    {
        UnstagedChanges.Clear();
        StagedChanges.Clear();

        foreach (var entry in entries)
        {
            var relPath = entry.FilePath.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(workDir, relPath));

            var stagedBadge = ToStagedBadge(entry.Status);
            if (stagedBadge != GitStatusBadge.None)
            {
                StagedChanges.Add(new GitChangesEntry(relPath, fullPath, stagedBadge));
            }

            var unstagedBadge = ToUnstagedBadge(entry.Status);
            if (unstagedBadge != GitStatusBadge.None)
            {
                UnstagedChanges.Add(new GitChangesEntry(relPath, fullPath, unstagedBadge));
            }
        }

        GitChangesStatusText = UnstagedChanges.Count + StagedChanges.Count > 0
            ? $"{UnstagedChanges.Count} unstaged, {StagedChanges.Count} staged"
            : "No changes";
    }

    private void ClearGitChangesCollections()
    {
        UnstagedChanges.Clear();
        StagedChanges.Clear();
        GitChangesStatusText = string.Empty;
    }

    private static GitStatusBadge ToStagedBadge(LibGit2Sharp.FileStatus status)
    {
        if (status.HasFlag(LibGit2Sharp.FileStatus.NewInIndex))       return GitStatusBadge.Added;
        if (status.HasFlag(LibGit2Sharp.FileStatus.ModifiedInIndex))  return GitStatusBadge.Modified;
        if (status.HasFlag(LibGit2Sharp.FileStatus.DeletedFromIndex)) return GitStatusBadge.Deleted;
        return GitStatusBadge.None;
    }

    private static GitStatusBadge ToUnstagedBadge(LibGit2Sharp.FileStatus status)
    {
        if (status.HasFlag(LibGit2Sharp.FileStatus.Conflicted))         return GitStatusBadge.Conflict;
        if (status.HasFlag(LibGit2Sharp.FileStatus.DeletedFromWorkdir)) return GitStatusBadge.Deleted;
        if (status.HasFlag(LibGit2Sharp.FileStatus.ModifiedInWorkdir))  return GitStatusBadge.Modified;
        if (status.HasFlag(LibGit2Sharp.FileStatus.NewInWorkdir))       return GitStatusBadge.Untracked;
        return GitStatusBadge.None;
    }

    /// <summary>
    /// Gets Quick Info (hover tooltip) for the symbol at the given editor offset.
    /// Returns null if no info is available or the file is not a C# file.
    /// Includes diagnostic messages when hovering over a squiggle.
    /// </summary>
    public async Task<QuickInfoResult?> GetQuickInfoAsync(int offset)
    {
        if (_editor is null || ActiveTab is null || !RoslynWorkspaceService.IsCSharpFile(ActiveTab.FilePath))
        {
            return null;
        }

        _quickInfoCts?.Cancel();
        _quickInfoCts?.Dispose();
        _quickInfoCts = new CancellationTokenSource();
        var ct = _quickInfoCts.Token;

        try
        {
            await _roslynService.UpdateDocumentTextAsync(ActiveTab.FilePath, _editor.Text, ct).ConfigureAwait(true);

            // Pass current diagnostics so hover over squiggles shows messages
            var diagnosticEntries = _diagnosticRenderer?.Entries;
            return await _quickInfoService.GetQuickInfoAsync(
                ActiveTab.FilePath, offset, diagnosticEntries, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Closes all open tabs, prompting to save dirty files. Returns false if the user cancels.
    /// </summary>
    private async Task<bool> CloseAllTabsAsync()
    {
        foreach (var tab in OpenTabs.Where(t => t.IsDirty).ToList())
        {
            ActiveTab = tab;
            var result = MessageBox.Show(
                $"Save changes to {tab.FileName}?",
                "Unsaved Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            switch (result)
            {
                case MessageBoxResult.Yes:
                    Save();
                    break;
                case MessageBoxResult.Cancel:
                    return false;
            }
        }

        foreach (var tab in OpenTabs.ToList())
        {
            await _roslynService.CloseDocumentAsync(tab.FilePath).ConfigureAwait(true);
        }

        OpenTabs.Clear();
        ActiveTab = null;
        DiagnosticItems.Clear();
        _diagnosticRenderer?.UpdateDiagnostics([]);
        _diagnosticStatusText = null;

        if (_editor is not null)
        {
            _editor.Document = new ICSharpCode.AvalonEdit.Document.TextDocument();
        }

        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(StatusText));

        return true;
    }

    /// <summary>
    /// Clears <see cref="LoadingStatus"/> after a delay so it doesn't permanently override status text.
    /// </summary>
    private void ScheduleLoadingStatusClear()
    {
        _loadingStatusClearCts?.Cancel();
        _loadingStatusClearCts = new CancellationTokenSource();
        var ct = _loadingStatusClearCts.Token;

        _ = ClearLoadingStatusAfterDelayAsync(ct);
    }

    private async Task ClearLoadingStatusAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(true);
            LoadingStatus = null;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer load or manual clear
        }
    }

    /// <summary>
    /// Cancels any in-flight project/solution load.
    /// </summary>
    private void CancelPreviousLoad()
    {
        if (_loadCts is not null)
        {
            _loadCts.Cancel();
            _loadCts.Dispose();
            _loadCts = null;
        }
    }

    private bool CanBuild() => !string.IsNullOrEmpty(_loadedProjectOrSolutionPath) && !_buildService.IsRunning;

    /// <summary>
    /// Builds the currently loaded project or solution.
    /// </summary>
    private async Task BuildProjectAsync()
    {
        if (string.IsNullOrEmpty(_loadedProjectOrSolutionPath))
        {
            return;
        }

        BuildOutputLines.Clear();
        BuildSummary = "Building...";
        BuildOutputLines.Add($"> dotnet build \"{_loadedProjectOrSolutionPath}\"");
        BuildOutputLines.Add(string.Empty);

        await _buildService.BuildAsync(_loadedProjectOrSolutionPath).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the currently loaded project.
    /// </summary>
    private async Task RunProjectAsync()
    {
        if (string.IsNullOrEmpty(_loadedProjectOrSolutionPath))
        {
            return;
        }

        var runnableProjectPath = ResolveRunnableProjectPath();

        BuildOutputLines.Clear();
        if (string.IsNullOrEmpty(runnableProjectPath))
        {
            BuildSummary = "Run failed";
            BuildOutputLines.Add("No runnable project (.csproj) could be resolved from the current selection.");
            BuildOutputLines.Add("Load a project directly or ensure the solution contains at least one C# project.");
            return;
        }

        BuildSummary = "Running...";
        BuildOutputLines.Add($"> dotnet run --project \"{runnableProjectPath}\"");
        BuildOutputLines.Add(string.Empty);

        await _buildService.RunAsync(runnableProjectPath).ConfigureAwait(false);
    }

    private string? ResolveRunnableProjectPath()
    {
        if (string.IsNullOrWhiteSpace(_loadedProjectOrSolutionPath))
        {
            return null;
        }

        var extension = Path.GetExtension(_loadedProjectOrSolutionPath);
        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return _loadedProjectOrSolutionPath;
        }

        if (!extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return DiscoverProjectPaths(_loadedProjectOrSolutionPath)
                .FirstOrDefault(File.Exists);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Cancels the currently running build/run process.
    /// </summary>
    private void CancelBuild()
    {
        _buildService.Cancel();
    }

    private void OnBuildOutputReceived(string line)
    {
        Application.Current.Dispatcher.BeginInvoke(() => BuildOutputLines.Add(line));
    }

    private void OnBuildProcessExited(int exitCode)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            BuildOutputLines.Add(string.Empty);
            BuildOutputLines.Add($"Process exited with code {exitCode}.");
            BuildSummary = exitCode == 0 ? "Build succeeded" : $"Build failed (exit code {exitCode})";
        });
    }

    public void Dispose()
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _buildService.OutputReceived -= OnBuildOutputReceived;
        _buildService.ProcessExited -= OnBuildProcessExited;
        _buildService.Dispose();
        _gitService.StatusChanged -= OnGitStatusChanged;
        _gitService.Dispose();
        DisposeExplorerWatcher();
        _explorerRefreshCts?.Cancel();
        _explorerRefreshCts?.Dispose();
        DisposeProjectFileWatcher();
        _projectFileRefreshCts?.Cancel();
        _projectFileRefreshCts?.Dispose();
        CancelPreviousLoad();
        _loadingStatusClearCts?.Cancel();
        _loadingStatusClearCts?.Dispose();
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _findReferencesCts?.Cancel();
        _findReferencesCts?.Dispose();
        _quickInfoCts?.Cancel();
        _quickInfoCts?.Dispose();
        _signatureHelpCts?.Cancel();
        _signatureHelpCts?.Dispose();
        _codeActionsCts?.Cancel();
        _codeActionsCts?.Dispose();
        _renameCts?.Cancel();
        _renameCts?.Dispose();
        _roslynService.Dispose();
    }
}
