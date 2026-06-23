namespace KaneCode.Controls;

/// <summary>
/// Base type for items in the mode selector dropdown of <see cref="AiChatPanel"/>.
/// </summary>
internal abstract class ModeDropdownItem
{
    /// <summary>
    /// True if this item can be selected in the dropdown.
    /// </summary>
    public abstract bool IsSelectable { get; }
}

/// <summary>
/// Represents a built-in <see cref="Services.Ai.IAiChatMode"/> item.
/// </summary>
internal sealed class ModeDropdownModeItem : ModeDropdownItem
{
    public Services.Ai.IAiChatMode Mode { get; init; } = null!;

    public override bool IsSelectable => true;
}

/// <summary>
/// Represents a user-defined preset item, wrapping an <see cref="Models.AiPreset"/>.
/// </summary>
internal sealed class ModeDropdownPresetItem : ModeDropdownItem
{
    public Models.AiPreset Preset { get; init; } = null!;

    public Services.Ai.IAiChatMode Mode { get; init; } = null!;

    public override bool IsSelectable => true;
}

/// <summary>
/// A non-selectable separator line.
/// </summary>
internal sealed class ModeDropdownSeparatorItem : ModeDropdownItem
{
    public override bool IsSelectable => false;
}

/// <summary>
/// A non-selectable section header (e.g. "Built-in", "Presets").
/// </summary>
internal sealed class ModeDropdownHeaderItem : ModeDropdownItem
{
    public string Text { get; init; } = string.Empty;

    public override bool IsSelectable => false;
}

/// <summary>
/// A selectable action item in the dropdown (e.g. "✏️ Edit presets...").
/// When selected, the registered action is invoked.
/// </summary>
internal sealed class ModeDropdownActionItem : ModeDropdownItem
{
    public string Text { get; init; } = string.Empty;

    public Action? Action { get; init; }

    public override bool IsSelectable => true;
}
