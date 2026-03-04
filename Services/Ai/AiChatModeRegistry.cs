namespace KaneCode.Services.Ai;

/// <summary>
/// Registry of available <see cref="IAiChatMode"/> instances.
/// Provides lookup by id and a default mode for new conversations.
/// </summary>
internal sealed class AiChatModeRegistry
{
    private readonly List<IAiChatMode> _modes = [];

    /// <summary>All registered modes in display order.</summary>
    public IReadOnlyList<IAiChatMode> Modes => _modes;

    /// <summary>
    /// Registers a mode. Modes are displayed in the order they are registered.
    /// Replaces any existing mode with the same <see cref="IAiChatMode.Id"/>.
    /// </summary>
    public void Register(IAiChatMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);

        var existing = _modes.FindIndex(m => m.Id == mode.Id);
        if (existing >= 0)
        {
            _modes[existing] = mode;
        }
        else
        {
            _modes.Add(mode);
        }
    }

    /// <summary>
    /// Returns the mode with the given id, or null if not found.
    /// </summary>
    public IAiChatMode? Get(string id)
    {
        return _modes.Find(m => m.Id == id);
    }

    /// <summary>
    /// Returns the first registered mode, or null if none are registered.
    /// </summary>
    public IAiChatMode? Default => _modes.Count > 0 ? _modes[0] : null;
}
