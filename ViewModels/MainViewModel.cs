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
    private RoslynClassificationColorizer? _classificationColorizer;
    private RoslynDiagnosticRenderer? _diagnosticRenderer;
    private SearchPanel? _searchPanel;
    private CompletionWindow? _completionWindow;
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource? _navigationCts;
    private CancellationTokenSource? _loadCts;
    private bool _isLoadingProject;
    private readonly TimeSpan _analysisDelay = TimeSpan.FromMilliseconds(500);
    private CancellationTokenSource? _loadingStatusClearCts;

    public MainViewModel()
    {
        ThemeManager.ThemeChanged += OnThemeChanged;
        _completionProvider = new RoslynCompletionProvider(_roslynService);
        _navigationService = new RoslynNavigationService(_roslynService);

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
        CloseTabCommand = new RelayCommand(param => CloseTab(param as OpenFileTab), _ => ActiveTab is not null);
        ExitCommand = new RelayCommand(_ => ExitApplication());
        OpenOptionsCommand = new RelayCommand(_ => OpenOptions());
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
    public ICommand CloseTabCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand OpenOptionsCommand { get; }

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
    /// Diagnostics shown in the error list panel, updated after each analysis pass.
    /// </summary>
    public ObservableCollection<DiagnosticItem> DiagnosticItems { get; } = [];

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

        _searchPanel = SearchPanel.Install(_editor.TextArea);

        _editor.TextArea.TextEntering += OnTextEntering;
        _editor.TextArea.TextEntered += OnTextEntered;

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
        var tab = new OpenFileTab("Untitled", string.Empty);
        OpenTabs.Add(tab);
        ActivateTab(tab, syntaxHighlighting: "C#");
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
            var root = EditorService.BuildFileTree(projectDir);
            ProjectItems = new ObservableCollection<ProjectItem>(root.Children);

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
            var root = EditorService.BuildFileTree(solutionDir);
            ProjectItems = new ObservableCollection<ProjectItem>(root.Children);

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

    public void LoadProjectRoot(string rootPath)
    {
        ArgumentNullException.ThrowIfNull(rootPath);

        ProjectRootPath = rootPath;
        var root = EditorService.BuildFileTree(rootPath);
        ProjectItems = new ObservableCollection<ProjectItem>(root.Children);
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

        if (!item.IsDirectory)
        {
            OpenFileByPath(item.FullPath);
        }
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
            if (!char.IsLetterOrDigit(e.Text[0]) && e.Text[0] != '_' && e.Text[0] != '.')
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

        // Trigger completion on '.' or Ctrl+Space style (auto on letter)
        if (e.Text.Length == 1 && RoslynCompletionProvider.ShouldTriggerCompletion(e.Text[0]))
        {
            // Only auto-trigger on '.' — letters require manual trigger or are handled by ongoing window
            if (e.Text[0] == '.' && _completionWindow is null)
            {
                await ShowCompletionWindowAsync().ConfigureAwait(true);
            }
        }
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
            var completions = await _completionProvider.GetCompletionsAsync(
                ActiveTab.FilePath, caretOffset).ConfigureAwait(true);

            if (completions.Count == 0)
            {
                return;
            }

            _completionWindow = new CompletionWindow(_editor.TextArea);

            if (Application.Current.TryFindResource(ThemeResourceKeys.CompletionBackground) is Brush bgBrush)
            {
                _completionWindow.Background = bgBrush;
            }

            foreach (var item in completions)
            {
                _completionWindow.CompletionList.CompletionData.Add(item);
            }

            _completionWindow.Show();
            _completionWindow.Closed += (_, _) => _completionWindow = null;
        }
        catch (OperationCanceledException)
        {
            // Ignored
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

            // Diagnostics
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

            // Build error list items with line/column info
            var document = _roslynService.GetDocument(filePath);
            var sourceText = document is not null
                ? await document.GetTextAsync(cancellationToken).ConfigureAwait(false)
                : null;

            var diagnosticItems = new List<DiagnosticItem>();
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

                diagnosticItems.Add(new DiagnosticItem(
                    entry.Severity, entry.Id, entry.Message,
                    fileName, line, column,
                    entry.Start, entry.End, filePath));
            }

            // Update UI on dispatcher
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _diagnosticRenderer?.UpdateDiagnostics(entries);
                _editor?.TextArea.TextView.Redraw();

                // Update error list panel
                DiagnosticItems.Clear();
                foreach (var item in diagnosticItems)
                {
                    DiagnosticItems.Add(item);
                }

                // Update status with diagnostic summary
                var errorCount = entries.Count(e => e.Severity == DiagnosticSeverity.Error);
                var warningCount = entries.Count(e => e.Severity == DiagnosticSeverity.Warning);
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

    public void Dispose()
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        CancelPreviousLoad();
        _loadingStatusClearCts?.Cancel();
        _loadingStatusClearCts?.Dispose();
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _roslynService.Dispose();
    }
}
