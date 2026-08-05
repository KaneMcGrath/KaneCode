using KaneCode.Services;

namespace KaneCode.Models;

/// <summary>
/// A selectable entry in the run button's launch-profile dropdown.
/// Wraps a <see cref="LaunchProfile"/> loaded from Properties/launchSettings.json,
/// or represents the default fallback used when no profiles exist or the file
/// cannot be read.
/// </summary>
public sealed class RunProfileOption
{
    /// <summary>
    /// The underlying launch profile, or null when this entry is the default fallback.
    /// </summary>
    internal LaunchProfile? Profile { get; }

    /// <summary>
    /// Display name shown on the run button and in the dropdown.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// True when this entry is the default fallback (no launch profile is applied
    /// and the project is run with plain <c>dotnet run</c>).
    /// </summary>
    public bool IsDefaultFallback => Profile is null;

    internal RunProfileOption(LaunchProfile? profile, string displayName)
    {
        Profile = profile;
        DisplayName = displayName;
    }

    /// <summary>
    /// Creates the default fallback entry used when a project has no launch
    /// profiles or launchSettings.json is missing or invalid.
    /// </summary>
    internal static RunProfileOption CreateDefault() => new(null, "(Default)");
}
