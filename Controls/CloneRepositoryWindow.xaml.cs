using System.Windows;
using KaneCode.Services;
using Microsoft.Win32;

namespace KaneCode.Controls;

public partial class CloneRepositoryWindow : Window
{
    internal string? RepositoryUrl { get; private set; }
    internal string? DestinationPath { get; private set; }

    public CloneRepositoryWindow()
    {
        InitializeComponent();
        DestinationTextBox.Text = GeneralSettingsManager.LoadDefaultProjectFolder();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new() { Title = "Select Clone Destination", InitialDirectory = DestinationTextBox.Text };
        if (dialog.ShowDialog() == true) DestinationTextBox.Text = dialog.FolderName;
    }

    private void Clone_Click(object sender, RoutedEventArgs e)
    {
        string url = UrlTextBox.Text.Trim();
        string destination = DestinationTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(destination))
        {
            MessageBox.Show(this, "Repository URL and destination are required.", "Clone Repository", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RepositoryUrl = url;
        DestinationPath = destination;
        DialogResult = true;
    }
}
