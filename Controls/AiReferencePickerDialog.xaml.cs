using KaneCode.Models;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KaneCode.Controls;

/// <summary>
/// Dialog for picking project files to add as AI chat references.
/// Shows a searchable flat list of all files in the project tree.
/// </summary>
public partial class AiReferencePickerDialog : Window
{
    private readonly List<string> _allFilePaths = [];

    /// <summary>
    /// The file paths selected by the user, populated after the dialog closes with OK.
    /// </summary>
    public List<string> SelectedFilePaths { get; } = [];

    public AiReferencePickerDialog(IReadOnlyList<ProjectItem> projectItems)
    {
        InitializeComponent();
        CollectFilePaths(projectItems);
        ApplyFilter(string.Empty);
        SearchBox.Focus();
    }

    private void CollectFilePaths(IReadOnlyList<ProjectItem> items)
    {
        foreach (var item in items)
        {
            if (item.ItemType == ProjectItemType.File && File.Exists(item.FullPath))
            {
                _allFilePaths.Add(item.FullPath);
            }

            if (item.Children.Count > 0)
            {
                CollectFilePaths(item.Children);
            }
        }
    }

    private void ApplyFilter(string filter)
    {
        var filtered = string.IsNullOrWhiteSpace(filter)
            ? _allFilePaths
            : _allFilePaths.Where(p =>
                Path.GetFileName(p).Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                p.Contains(filter, StringComparison.OrdinalIgnoreCase))
              .ToList();

        FileList.ItemsSource = filtered;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter(SearchBox.Text);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        AddSelectedAndClose();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        AddSelectedAndClose();
    }

    private void AddSelectedAndClose()
    {
        if (FileList.SelectedItems.Count == 0)
        {
            return;
        }

        foreach (var item in FileList.SelectedItems)
        {
            if (item is string path)
            {
                SelectedFilePaths.Add(path);
            }
        }

        DialogResult = true;
        Close();
    }
}
