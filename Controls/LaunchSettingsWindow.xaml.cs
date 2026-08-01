using KaneCode.Services;
using System.Windows;
using System.Windows.Controls;

namespace KaneCode.Controls;

public partial class LaunchSettingsWindow : Window
{
    private readonly string _projectPath;
    private readonly List<LaunchProfile> _profiles;
    private bool _loading;

    public LaunchSettingsWindow(string projectPath)
    {
        InitializeComponent();
        _projectPath = projectPath;
        _profiles = LaunchSettingsService.Load(projectPath);
        if (_profiles.Count == 0) _profiles.Add(new LaunchProfile { Name = "Project" });
        ProfilesList.ItemsSource = _profiles;
        ProfilesList.SelectedIndex = 0;
    }

    private LaunchProfile? Current => ProfilesList.SelectedItem as LaunchProfile;

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Current is null) return;
        _loading = true;
        NameBox.Text = Current.Name;
        CommandBox.SelectedItem = CommandBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), Current.CommandName, StringComparison.OrdinalIgnoreCase));
        if (CommandBox.SelectedIndex < 0) CommandBox.SelectedIndex = 0;
        ArgumentsBox.Text = Current.CommandLineArgs;
        WorkingDirectoryBox.Text = Current.WorkingDirectory;
        ExecutableBox.Text = Current.ExecutablePath;
        LaunchUrlBox.Text = Current.LaunchUrl;
        LaunchBrowserBox.IsChecked = Current.LaunchBrowser;
        EnvironmentBox.Text = string.Join(Environment.NewLine, Current.EnvironmentVariables.Select(p => $"{p.Key}={p.Value}"));
        _loading = false;
    }

    private void EditorChanged(object sender, RoutedEventArgs e) => UpdateCurrent();
    private void EditorChanged(object sender, SelectionChangedEventArgs e) => UpdateCurrent();

    private void UpdateCurrent()
    {
        if (_loading || Current is null) return;
        Current.Name = NameBox.Text.Trim();
        Current.CommandName = (CommandBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Project";
        Current.CommandLineArgs = ArgumentsBox.Text;
        Current.WorkingDirectory = WorkingDirectoryBox.Text;
        Current.ExecutablePath = ExecutableBox.Text;
        Current.LaunchUrl = LaunchUrlBox.Text;
        Current.LaunchBrowser = LaunchBrowserBox.IsChecked == true;
        Current.EnvironmentVariables.Clear();
        foreach (string line in EnvironmentBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=');
            if (separator > 0) Current.EnvironmentVariables[line[..separator].Trim()] = line[(separator + 1)..];
        }
        ProfilesList.Items.Refresh();
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        string name = "NewProfile";
        int suffix = 2;
        while (_profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) name = $"NewProfile{suffix++}";
        LaunchProfile profile = new() { Name = name };
        _profiles.Add(profile);
        ProfilesList.Items.Refresh();
        ProfilesList.SelectedItem = profile;
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Current is null || _profiles.Count == 1) return;
        _profiles.Remove(Current);
        ProfilesList.Items.Refresh();
        ProfilesList.SelectedIndex = 0;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        UpdateCurrent();
        if (_profiles.Any(p => string.IsNullOrWhiteSpace(p.Name) || _profiles.Count(q => string.Equals(q.Name, p.Name, StringComparison.OrdinalIgnoreCase)) > 1))
        {
            MessageBox.Show(this, "Profile names must be unique and cannot be empty.", "Launch Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try { LaunchSettingsService.Save(_projectPath, _profiles); DialogResult = true; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Launch Settings", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
