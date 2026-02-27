using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace KaneCode.Infrastructure;

/// <summary>
/// Central registry for all IDE hotkey bindings.
/// Manages defaults, user overrides, persistence, and change notifications.
/// </summary>
internal static class HotkeyManager
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KaneCode");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "hotkeys.json");

    private static readonly Dictionary<HotkeyAction, HotkeyBinding> _bindings = new();

    /// <summary>
    /// Raised after any binding changes so the UI can re-apply input bindings.
    /// </summary>
    public static event Action? BindingsChanged;

    /// <summary>
    /// All registered bindings.
    /// </summary>
    public static IReadOnlyCollection<HotkeyBinding> Bindings => _bindings.Values;

    /// <summary>
    /// Initializes the default bindings and loads user overrides from disk.
    /// Call once at application startup.
    /// </summary>
    public static void Initialize()
    {
        RegisterDefaults();
        LoadOverrides();
    }

    /// <summary>
    /// Gets the binding for a specific action.
    /// </summary>
    public static HotkeyBinding Get(HotkeyAction action)
    {
        return _bindings[action];
    }

    /// <summary>
    /// Gets the display gesture text for a specific action (e.g. "Ctrl+S").
    /// </summary>
    public static string GetGestureText(HotkeyAction action)
    {
        return _bindings.TryGetValue(action, out var binding) ? binding.GestureText : string.Empty;
    }

    /// <summary>
    /// Updates the gesture for a specific action and persists the change.
    /// </summary>
    public static void SetGesture(HotkeyAction action, Key key, ModifierKeys modifiers)
    {
        if (!_bindings.TryGetValue(action, out var binding))
        {
            return;
        }

        binding.Key = key;
        binding.Modifiers = modifiers;
        SaveOverrides();
        BindingsChanged?.Invoke();
    }

    /// <summary>
    /// Resets a single action to its default gesture and persists the change.
    /// </summary>
    public static void ResetToDefault(HotkeyAction action)
    {
        if (!_bindings.TryGetValue(action, out var binding))
        {
            return;
        }

        binding.ResetToDefault();
        SaveOverrides();
        BindingsChanged?.Invoke();
    }

    /// <summary>
    /// Resets all bindings to defaults and persists.
    /// </summary>
    public static void ResetAllToDefaults()
    {
        foreach (var binding in _bindings.Values)
        {
            binding.ResetToDefault();
        }

        SaveOverrides();
        BindingsChanged?.Invoke();
    }

    /// <summary>
    /// Persists current bindings to disk. Only custom (non-default) bindings are saved.
    /// </summary>
    public static void SaveOverrides()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            var overrides = _bindings.Values
                .Where(b => b.IsCustom)
                .ToDictionary(
                    b => b.Action.ToString(),
                    b => new HotkeyOverrideDto { Key = b.Key.ToString(), Modifiers = b.Modifiers.ToString() });

            var json = JsonSerializer.Serialize(overrides, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (IOException)
        {
            // Best effort — don't crash if settings can't be saved
        }
    }

    private static void LoadOverrides()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            var overrides = JsonSerializer.Deserialize<Dictionary<string, HotkeyOverrideDto>>(json);
            if (overrides is null)
            {
                return;
            }

            foreach (var (actionName, dto) in overrides)
            {
                if (Enum.TryParse<HotkeyAction>(actionName, out var action)
                    && _bindings.TryGetValue(action, out var binding)
                    && Enum.TryParse<Key>(dto.Key, out var key)
                    && Enum.TryParse<ModifierKeys>(dto.Modifiers, out var modifiers))
                {
                    binding.Key = key;
                    binding.Modifiers = modifiers;
                }
            }
        }
        catch (JsonException)
        {
            // Corrupted file — ignore and use defaults
        }
        catch (IOException)
        {
            // Can't read — use defaults
        }
    }

    private static void RegisterDefaults()
    {
        _bindings.Clear();
        Register(HotkeyAction.NewFile, "New File", Key.N, ModifierKeys.Control);
        Register(HotkeyAction.OpenFile, "Open File", Key.O, ModifierKeys.Control);
        Register(HotkeyAction.OpenFolder, "Open Folder", Key.None, ModifierKeys.None);
        Register(HotkeyAction.OpenProject, "Open Project", Key.None, ModifierKeys.None);
        Register(HotkeyAction.OpenSolution, "Open Solution", Key.None, ModifierKeys.None);
        Register(HotkeyAction.Save, "Save", Key.S, ModifierKeys.Control);
        Register(HotkeyAction.SaveAs, "Save As", Key.S, ModifierKeys.Control | ModifierKeys.Shift);
        Register(HotkeyAction.CloseTab, "Close Tab", Key.W, ModifierKeys.Control);
        Register(HotkeyAction.Undo, "Undo", Key.Z, ModifierKeys.Control);
        Register(HotkeyAction.Redo, "Redo", Key.Y, ModifierKeys.Control);
        Register(HotkeyAction.Cut, "Cut", Key.X, ModifierKeys.Control);
        Register(HotkeyAction.Copy, "Copy", Key.C, ModifierKeys.Control);
        Register(HotkeyAction.Paste, "Paste", Key.V, ModifierKeys.Control);
        Register(HotkeyAction.Find, "Find", Key.F, ModifierKeys.Control);
        Register(HotkeyAction.Replace, "Replace", Key.H, ModifierKeys.Control);
        Register(HotkeyAction.GoToDefinition, "Go to Definition", Key.F12, ModifierKeys.None);
        Register(HotkeyAction.TriggerCompletion, "Trigger Completion", Key.Space, ModifierKeys.Control);
        Register(HotkeyAction.OpenOptions, "Options", Key.None, ModifierKeys.None);
        Register(HotkeyAction.Exit, "Exit", Key.None, ModifierKeys.None);
        Register(HotkeyAction.BuildProject, "Build Project", Key.B, ModifierKeys.Control | ModifierKeys.Shift);
        Register(HotkeyAction.RunProject, "Run Project", Key.F5, ModifierKeys.None);
        Register(HotkeyAction.CancelBuild, "Cancel Build", Key.None, ModifierKeys.None);
    }

    private static void Register(HotkeyAction action, string displayName, Key key, ModifierKeys modifiers)
    {
        _bindings[action] = new HotkeyBinding(action, displayName, key, modifiers);
    }

    private sealed class HotkeyOverrideDto
    {
        public string Key { get; set; } = string.Empty;
        public string Modifiers { get; set; } = string.Empty;
    }
}
