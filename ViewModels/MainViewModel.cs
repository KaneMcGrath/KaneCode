using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using KaneCode.Infrastructure;
using KaneCode.Models;
using KaneCode.Services;
using KaneCode.Theming;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace KaneCode.ViewModels;

/// <summary>
/// Main view model that orchestrates project loading, file opening, editing, and saving.
/// </summary>
internal sealed class MainViewModel : ObservableObject
{
    private TextEditor? _editor;
    private bool _isActivating;

    public MainViewModel()
    {
        NewFileCommand = new RelayCommand(_ => NewFile());
        OpenFileCommand = new RelayCommand(_ => OpenFile());
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());
        SaveCommand = new RelayCommand(_ => Save(), _ => ActiveTab is not null);
        SaveAsCommand = new RelayCommand(_ => SaveAs(), _ => ActiveTab is not null);
        UndoCommand = new RelayCommand(_ => _editor?.Undo(), _ => _editor?.CanUndo == true);
        RedoCommand = new RelayCommand(_ => _editor?.Redo(), _ => _editor?.CanRedo == true);
        CutCommand = new RelayCommand(_ => _editor?.Cut(), _ => _editor is not null);
        CopyCommand = new RelayCommand(_ => _editor?.Copy(), _ => _editor is not null);
        PasteCommand = new RelayCommand(_ => _editor?.Paste(), _ => _editor is not null);
        CloseTabCommand = new RelayCommand(param => CloseTab(param as OpenFileTab), _ => ActiveTab is not null);
        ExitCommand = new RelayCommand(_ => ExitApplication());
        SetDarkThemeCommand = new RelayCommand(_ => ThemeManager.ApplyTheme(AppTheme.Dark));
        SetLightThemeCommand = new RelayCommand(_ => ThemeManager.ApplyTheme(AppTheme.Light));
    }

    public ICommand NewFileCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand CutCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand SetDarkThemeCommand { get; }
    public ICommand SetLightThemeCommand { get; }

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

    public ObservableCollection<OpenFileTab> OpenTabs { get; } = [];

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

    public string StatusText => ActiveTab is not null
        ? $"Editing: {ActiveTab.FilePath}"
        : "Ready";

    public void AttachEditor(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editor = editor;
        _editor.TextChanged += OnEditorTextChanged;
    }

    private void NewFile()
    {
        var tab = new OpenFileTab("Untitled") { IsDirty = false };
        OpenTabs.Add(tab);
        ActivateTab(tab, content: string.Empty, syntaxHighlighting: "C#");
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
            var tab = new OpenFileTab(filePath);
            OpenTabs.Add(tab);
            ActivateTab(tab, content, highlighting);
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
            EditorService.WriteFile(dialog.FileName, _editor.Text);

            if (ActiveTab is not null)
            {
                OpenTabs.Remove(ActiveTab);
            }

            var newTab = new OpenFileTab(dialog.FileName);
            OpenTabs.Add(newTab);
            ActivateTab(newTab);
        }
        catch (IOException ex)
        {
            MessageBox.Show($"Could not save file:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseTab(OpenFileTab? tab)
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

        if (OpenTabs.Count > 0)
        {
            ActivateTab(OpenTabs[^1]);
        }
        else
        {
            ActiveTab = null;
            _editor?.Clear();
            OnPropertyChanged(nameof(StatusText));
        }
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

    private readonly Dictionary<string, string> _tabContentCache = new(StringComparer.OrdinalIgnoreCase);

    private void ActivateTab(OpenFileTab tab, string? content = null, string? syntaxHighlighting = null)
    {
        if (_editor is null)
        {
            return;
        }

        if (ActiveTab is not null && !_isActivating)
        {
            _tabContentCache[ActiveTab.FilePath] = _editor.Text;
        }

        _isActivating = true;
        try
        {
            ActiveTab = tab;

            if (content is not null)
            {
                _editor.Text = content;
                _tabContentCache[tab.FilePath] = content;
            }
            else if (_tabContentCache.TryGetValue(tab.FilePath, out var cached))
            {
                _editor.Text = cached;
            }
            else
            {
                _editor.Text = EditorService.ReadFile(tab.FilePath);
                _tabContentCache[tab.FilePath] = _editor.Text;
            }

            var hlName = syntaxHighlighting ?? EditorService.GetSyntaxHighlighting(tab.FilePath);
            _editor.SyntaxHighlighting = hlName is not null
                ? HighlightingManager.Instance.GetDefinition(hlName)
                : null;

            tab.IsDirty = false;
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
}
