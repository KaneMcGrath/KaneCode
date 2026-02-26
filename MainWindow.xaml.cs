using KaneCode.Models;
using KaneCode.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KaneCode;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

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
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Dispose();
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