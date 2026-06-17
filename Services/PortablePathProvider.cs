using System.IO;

namespace KaneCode.Services;

/// <summary>
/// Determines the base directory for all application settings.
/// 
/// <para>
/// <b>Portable mode:</b> If a folder named "portable" exists alongside the application
/// executable, all settings are stored inside that folder. This enables the application
/// to run from a USB drive or cloud-synced folder with no registry or AppData dependencies.
/// </para>
/// 
/// <para>
/// <b>Standard mode:</b> Otherwise, settings go to <c>%LocalAppData%\KaneCode\</c>.
/// </para>
/// 
/// <para>
/// The portable folder is checked once at type initialization (before any settings
/// manager reads or writes), so the result is consistent throughout the application
/// lifetime. Call <see cref="EnsureInitialized"/> from <c>App.OnStartup</c> to guarantee
/// the check runs before any settings code executes.
/// </para>
/// </summary>
internal static class PortablePathProvider
{
    private static readonly string _baseDirectory;

    /// <summary>
    /// Static constructor — runs once before any member is accessed.
    /// Determines whether portable mode is active and computes the base path.
    /// </summary>
    static PortablePathProvider()
    {
        // Use AppContext.BaseDirectory instead of Assembly.Location so that
        // the check works correctly even when the application is published
        // as a single-file bundle.
        string appDir = AppContext.BaseDirectory;

        string portableDir = Path.Combine(appDir, "portable");
        if (Directory.Exists(portableDir))
        {
            _baseDirectory = portableDir;
            return;
        }

        // Fall back to per-user AppData folder
        _baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KaneCode");
    }

    /// <summary>
    /// The root directory for all settings files.
    /// In portable mode this is the <c>portable</c> folder next to the executable;
    /// in standard mode it is <c>%LocalAppData%\KaneCode</c>.
    /// </summary>
    public static string BaseDirectory => _baseDirectory;

    /// <summary>
    /// Returns <c>true</c> when the application is running in portable mode,
    /// meaning all settings are stored alongside the executable in a <c>portable</c> folder.
    /// </summary>
    public static bool IsPortable =>
        !string.Equals(
            _baseDirectory,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KaneCode"),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ensures the static initializer has run. Call this early in <c>App.OnStartup</c>
    /// to guarantee that the portable-folder check completes before any settings
    /// manager reads or writes its files.
    /// </summary>
    public static void EnsureInitialized()
    {
        // The static constructor runs on first access to any member of this class.
        // This method exists solely to guarantee that access happens early.
        _ = _baseDirectory;
    }
}
