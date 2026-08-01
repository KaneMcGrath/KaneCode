using System.Windows;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace KaneCode.Controls;

public partial class GitCredentialsWindow : Window
{
    internal CredentialsHandler? CredentialsProvider { get; private set; }

    public GitCredentialsWindow()
    {
        InitializeComponent();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        string username = UsernameBox.Text.Trim();
        string token = TokenBox.Password;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(token))
        {
            MessageBox.Show(this, "Username and token are required.", "Git Authentication", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials { Username = username, Password = token };
        DialogResult = true;
    }
}
