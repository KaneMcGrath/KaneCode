using System.Windows.Input;

namespace KaneCode.Infrastructure;

/// <summary>
/// Identifies each bindable IDE action.
/// </summary>
internal enum HotkeyAction
{
    NewFile,
    OpenFile,
    OpenFolder,
    OpenProject,
    OpenSolution,
    Save,
    SaveAs,
    CloseTab,
    Undo,
    Redo,
    Cut,
    Copy,
    Paste,
    Find,
    Replace,
    GoToDefinition,
    TriggerCompletion,
    OpenOptions,
    Exit
}

/// <summary>
/// Represents a single hotkey binding with a display name, default gesture, and current (possibly user-overridden) gesture.
/// </summary>
internal sealed class HotkeyBinding : ObservableObject
{
    private Key _key;
    private ModifierKeys _modifiers;

    public HotkeyBinding(HotkeyAction action, string displayName, Key defaultKey, ModifierKeys defaultModifiers)
    {
        Action = action;
        DisplayName = displayName;
        DefaultKey = defaultKey;
        DefaultModifiers = defaultModifiers;
        _key = defaultKey;
        _modifiers = defaultModifiers;
    }

    public HotkeyAction Action { get; }

    public string DisplayName { get; }

    public Key DefaultKey { get; }

    public ModifierKeys DefaultModifiers { get; }

    public Key Key
    {
        get => _key;
        set
        {
            if (SetProperty(ref _key, value))
            {
                OnPropertyChanged(nameof(GestureText));
                OnPropertyChanged(nameof(IsCustom));
            }
        }
    }

    public ModifierKeys Modifiers
    {
        get => _modifiers;
        set
        {
            if (SetProperty(ref _modifiers, value))
            {
                OnPropertyChanged(nameof(GestureText));
                OnPropertyChanged(nameof(IsCustom));
            }
        }
    }

    /// <summary>
    /// Human-readable text for the current gesture (e.g. "Ctrl+S").
    /// </summary>
    public string GestureText => FormatGesture(Modifiers, Key);

    /// <summary>
    /// Human-readable text for the default gesture.
    /// </summary>
    public string DefaultGestureText => FormatGesture(DefaultModifiers, DefaultKey);

    /// <summary>
    /// Whether the current binding differs from the default.
    /// </summary>
    public bool IsCustom => Key != DefaultKey || Modifiers != DefaultModifiers;

    /// <summary>
    /// Resets to the default gesture.
    /// </summary>
    public void ResetToDefault()
    {
        Key = DefaultKey;
        Modifiers = DefaultModifiers;
    }

    /// <summary>
    /// Formats a modifier+key combination into a display string.
    /// </summary>
    internal static string FormatGesture(ModifierKeys modifiers, Key key)
    {
        if (key == Key.None)
        {
            return string.Empty;
        }

        var parts = new List<string>(4);

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(key.ToString());
        return string.Join("+", parts);
    }
}
