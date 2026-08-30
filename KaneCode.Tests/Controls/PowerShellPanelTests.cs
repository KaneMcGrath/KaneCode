using KaneCode.Controls;
using System.Windows.Input;

namespace KaneCode.Tests.Controls;

public sealed class PowerShellPanelTests
{
    [Theory]
    [InlineData(Key.Tab, ModifierKeys.None, "\t")]
    [InlineData(Key.Tab, ModifierKeys.Shift, "\x1b[Z")]
    [InlineData(Key.Home, ModifierKeys.None, "\x1b[H")]
    [InlineData(Key.End, ModifierKeys.None, "\x1b[F")]
    public void WhenNavigationKeyWouldLeaveTerminalThenVtInputIsReturned(
        Key key,
        ModifierKeys modifiers,
        string expectedInput)
    {
        bool handled = PowerShellPanel.TryGetTerminalNavigationInput(key, modifiers, out string? input);

        Assert.True(handled);
        Assert.Equal(expectedInput, input);
    }

    [Theory]
    [InlineData(Key.Home, ModifierKeys.Control)]
    [InlineData(Key.End, ModifierKeys.Alt)]
    [InlineData(Key.Tab, ModifierKeys.Windows)]
    [InlineData(Key.Enter, ModifierKeys.None)]
    public void WhenKeyIsNotTerminalNavigationThenNoInputIsReturned(Key key, ModifierKeys modifiers)
    {
        bool handled = PowerShellPanel.TryGetTerminalNavigationInput(key, modifiers, out string? input);

        Assert.False(handled);
        Assert.Null(input);
    }
}
