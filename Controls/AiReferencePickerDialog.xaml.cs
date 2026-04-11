using KaneCode.Models;
using KaneCode.Services.Ai;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KaneCode.Controls;

/// <summary>
/// Dialog for picking built-in, project, and external sources to add as AI chat context.
/// </summary>
public partial class AiReferencePickerDialog : Window
{
    private readonly List<DefaultContextOption> _defaultOptions = [];
    private readonly List<AiContextClassSnapshot> _allClasses = [];
    private readonly ObservableCollection<AiChatReference> _externalReferences = [];

    /// <summary>
    /// The references selected by the user, populated after the dialog closes with OK.
    /// </summary>
    internal List<AiChatReference> SelectedReferences { get; } = [];

    internal AiReferencePickerDialog(
        IReadOnlyList<ProjectItem> projectItems,
        AiContextDocumentSnapshot? currentDocument,
        IReadOnlyList<AiContextDocumentSnapshot> openDocuments,
        AiBuildOutputSnapshot? buildOutput)
    {
        InitializeComponent();

        ArgumentNullException.ThrowIfNull(projectItems);
        ArgumentNullException.ThrowIfNull(openDocuments);

        BuildDefaultOptions(currentDocument, openDocuments, buildOutput);
        _allClasses.AddRange(AiContextReferenceFactory.DiscoverClasses(projectItems));

        DefaultContextList.ItemsSource = _defaultOptions;
        ExternalContextList.ItemsSource = _externalReferences;
        ApplyClassFilter(string.Empty);
        ClassSearchBox.Focus();
    }

    private void BuildDefaultOptions(
        AiContextDocumentSnapshot? currentDocument,
        IReadOnlyList<AiContextDocumentSnapshot> openDocuments,
        AiBuildOutputSnapshot? buildOutput)
    {
        _defaultOptions.Add(new DefaultContextOption(
            "Current document",
            currentDocument is null
                ? "No active document is available."
                : $"Attach the active editor document: {currentDocument.FilePath}",
            currentDocument is not null,
            currentDocument is null
                ? null
                : () => AiContextReferenceFactory.CreateCurrentDocumentReference(currentDocument)));

        _defaultOptions.Add(new DefaultContextOption(
            "All open documents",
            openDocuments.Count == 0
                ? "There are no open documents to attach."
                : $"Attach the text of all {openDocuments.Count} open documents.",
            openDocuments.Count > 0,
            openDocuments.Count == 0
                ? null
                : () => AiContextReferenceFactory.CreateAllOpenDocumentsReference(openDocuments)));

        _defaultOptions.Add(new DefaultContextOption(
            "Build output",
            buildOutput is null || (string.IsNullOrWhiteSpace(buildOutput.Summary) && buildOutput.Lines.Count == 0)
                ? "There is no build output available yet."
                : "Attach the current build output panel contents.",
            buildOutput is not null && (!string.IsNullOrWhiteSpace(buildOutput.Summary) || buildOutput.Lines.Count > 0),
            buildOutput is null
                ? null
                : () => AiContextReferenceFactory.CreateBuildOutputReference(buildOutput)));
    }

    private void ApplyClassFilter(string filter)
    {
        IReadOnlyList<AiContextClassSnapshot> filteredClasses = string.IsNullOrWhiteSpace(filter)
            ? _allClasses
            : _allClasses
                .Where(item => item.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || item.FilePath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

        ClassList.ItemsSource = filteredClasses;
    }

    private void ClassSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyClassFilter(ClassSearchBox.Text);
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

    private void ClassList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        AddSelectedAndClose();
    }

    private void AddExternalFileButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "Add External File",
            CheckFileExists = true,
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (string fileName in dialog.FileNames)
        {
            AddExternalReference(AiContextReferenceFactory.CreateFileReference(fileName));
        }
    }

    private void AddExternalFolderButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Add External Folder"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        AddExternalReference(AiContextReferenceFactory.CreateExternalFolderReference(dialog.FolderName));
    }

    private void AddDocumentationButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            "Documentation context is not available yet.",
            "Add Documentation",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void RemoveExternalReferenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AiChatReference reference })
        {
            _externalReferences.Remove(reference);
        }
    }

    private void AddExternalReference(AiChatReference reference)
    {
        if (_externalReferences.Any(existingReference => AreSameReference(existingReference, reference)))
        {
            return;
        }

        _externalReferences.Add(reference);
    }

    private void AddSelectedAndClose()
    {
        SelectedReferences.Clear();

        foreach (DefaultContextOption option in _defaultOptions.Where(item => item.IsSelected && item.IsAvailable))
        {
            AiChatReference? reference = option.CreateReference?.Invoke();
            if (reference is not null)
            {
                AddSelectedReference(reference);
            }
        }

        foreach (AiContextClassSnapshot classSnapshot in ClassList.SelectedItems.OfType<AiContextClassSnapshot>())
        {
            AddSelectedReference(AiContextReferenceFactory.CreateClassReference(classSnapshot));
        }

        foreach (AiChatReference reference in _externalReferences)
        {
            AddSelectedReference(reference);
        }

        if (SelectedReferences.Count == 0)
        {
            return;
        }

        DialogResult = true;
        Close();
    }

    private void AddSelectedReference(AiChatReference reference)
    {
        if (SelectedReferences.Any(existingReference => AreSameReference(existingReference, reference)))
        {
            return;
        }

        SelectedReferences.Add(reference);
    }

    private static bool AreSameReference(AiChatReference left, AiChatReference right)
    {
        if (left.Kind != right.Kind)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(left.FullPath) || !string.IsNullOrWhiteSpace(right.FullPath))
        {
            return string.Equals(left.FullPath, right.FullPath, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DefaultContextOption(
        string title,
        string description,
        bool isAvailable,
        Func<AiChatReference>? createReference)
    {
        public string Title { get; } = title;

        public string Description { get; } = description;

        public bool IsAvailable { get; } = isAvailable;

        public Func<AiChatReference>? CreateReference { get; } = createReference;

        public bool IsSelected { get; set; }
    }
}
