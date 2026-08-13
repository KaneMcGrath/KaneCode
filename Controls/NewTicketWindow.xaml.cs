using System.Globalization;
using System.Windows;

namespace KaneCode.Controls;

/// <summary>
/// Dialog for creating a new KaneCode ticket. All fields except Title and
/// Description are optional; blank optional fields inherit the IDE's active
/// provider, model, and agent mode when the ticket is dispatched.
/// </summary>
public partial class NewTicketWindow : Window
{
    public string TicketTitle => TitleTextBox.Text.Trim();

    public string TicketDescription => DescriptionTextBox.Text;

    public string? Provider
    {
        get
        {
            string value = ProviderComboBox.Text.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    public string? Model
    {
        get
        {
            string value = ModelComboBox.Text.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    public string? AgentMode
    {
        get
        {
            string value = AgentModeComboBox.Text.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    public int Priority =>
        int.TryParse(PriorityTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;

    public string? StartAfter
    {
        get
        {
            string value = StartAfterTextBox.Text.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    public NewTicketWindow(
        IReadOnlyList<string> providerOptions,
        IReadOnlyList<string> modelOptions,
        IReadOnlyList<string> modeOptions,
        Window? owner)
    {
        InitializeComponent();
        Owner = owner;

        ProviderComboBox.ItemsSource = providerOptions;
        ModelComboBox.ItemsSource = modelOptions;
        AgentModeComboBox.ItemsSource = modeOptions;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TicketTitle))
        {
            MessageBox.Show(this, "A ticket title is required.", "New Ticket",
                MessageBoxButton.OK, MessageBoxImage.Information);
            TitleTextBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
