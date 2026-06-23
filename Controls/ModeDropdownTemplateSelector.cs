using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KaneCode.Controls;

/// <summary>
/// <see cref="DataTemplateSelector"/> that picks the appropriate template for each
/// <see cref="ModeDropdownItem"/> subtype in the AI mode selector ComboBox.
/// </summary>
internal sealed class ModeDropdownTemplateSelector : DataTemplateSelector
{
    /// <summary>
    /// DataTemplate key suffix for header items (uses "ModeDropdownHeaderTemplate").
    /// </summary>
    private const string HeaderTemplateKey = "ModeDropdownHeaderTemplate";

    /// <summary>
    /// DataTemplate key suffix for separator items (uses "ModeDropdownSeparatorTemplate").
    /// </summary>
    private const string SeparatorTemplateKey = "ModeDropdownSeparatorTemplate";

    /// <summary>
    /// DataTemplate key suffix for mode items (uses "ModeDropdownModeTemplate").
    /// </summary>
    private const string ModeTemplateKey = "ModeDropdownModeTemplate";

    /// <summary>
    /// DataTemplate key suffix for preset items (uses "ModeDropdownPresetTemplate").
    /// </summary>
    private const string PresetTemplateKey = "ModeDropdownPresetTemplate";

    /// <summary>
    /// DataTemplate key suffix for action items (uses "ModeDropdownActionTemplate").
    /// </summary>
    private const string ActionTemplateKey = "ModeDropdownActionTemplate";

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is FrameworkElement fe && fe.TemplatedParent is not null)
        {
            // We're in the visual tree already — no need to switch
            return base.SelectTemplate(item, container);
        }

        if (container is not FrameworkElement element)
        {
            return null;
        }

        return item switch
        {
            ModeDropdownHeaderItem => TryFindTemplate(element, HeaderTemplateKey),
            ModeDropdownSeparatorItem => TryFindTemplate(element, SeparatorTemplateKey),
            ModeDropdownPresetItem => TryFindTemplate(element, PresetTemplateKey),
            ModeDropdownModeItem => TryFindTemplate(element, ModeTemplateKey),
            ModeDropdownActionItem => TryFindTemplate(element, ActionTemplateKey),
            _ => null
        };
    }

    private static DataTemplate? TryFindTemplate(FrameworkElement element, string key)
    {
        // Search up the visual tree
        DependencyObject current = element;

        while (current is not null)
        {
            if (current is FrameworkElement fe)
            {
                object? found = fe.TryFindResource(key);
                if (found is DataTemplate dt)
                {
                    return dt;
                }
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
