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

        // ── Portable mode check ──────────────────────────────────────────
        // Check for a "portable" folder alongside the executable before any
        // settings are loaded. If present, all settings are stored in that
        // folder instead of %LocalAppData%\KaneCode.
        PortablePathProvider.EnsureInitialized();

        // Create the settings directory if it doesn't already exist.
        // In portable mode the user is expected to create the "portable" folder
        // themselves; we create it only when running in standard mode so that
        // the LocalAppData path is guaranteed to exist when settings are saved,
        // and in portable mode we also create the directory so that default
        // settings files can be written on first launch.
        System.IO.Directory.CreateDirectory(PortablePathProvider.BaseDirectory);

        // Must register MSBuild before any MSBuild types are loaded
        MSBuildProjectLoader.EnsureMSBuildRegistered();

        HotkeyManager.Initialize();
    }
}
