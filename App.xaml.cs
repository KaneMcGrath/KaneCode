using KaneCode.Infrastructure;
using KaneCode.Services;
using System.Windows;

namespace KaneCode;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Command-line arguments passed to the application.
    /// Populated during <see cref="OnStartup"/> for retrieval after the main window loads.
    /// </summary>
    internal static string[] CommandLineArgs { get; private set; } = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Store command line arguments for later processing after the main window loads
        CommandLineArgs = e.Args;

        // Must register MSBuild before any MSBuild types are loaded
        MSBuildProjectLoader.EnsureMSBuildRegistered();

        HotkeyManager.Initialize();
    }
}
