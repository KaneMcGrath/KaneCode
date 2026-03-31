using System.Windows.Input;
using KaneCode.Infrastructure;

namespace KaneCode.Tests.Infrastructure;

public class HotkeyBindingTests
{
    [Fact]
    public void WhenConstructedThenDefaultsAreSet()
    {
        HotkeyBinding binding = new HotkeyBinding(HotkeyAction.Save, "Save", Key.S, ModifierKeys.Control);

        Assert.Equal(HotkeyAction.Save, binding.Action);
        Assert.Equal("Save", binding.DisplayName);
        Assert.Equal(Key.S, binding.DefaultKey);
        Assert.Equal(ModifierKeys.Control, binding.DefaultModifiers);
        Assert.Equal(Key.S, binding.Key);
        Assert.Equal(ModifierKeys.Control, binding.Modifiers);
    }

    [Fact]
    public void WhenKeyChangedThenIsCustomReturnsTrue()
    {
        HotkeyBinding binding = new HotkeyBinding(HotkeyAction.Save, "Save", Key.S, ModifierKeys.Control);

        binding.Key = Key.W;

        Assert.True(binding.IsCustom);
    }

    [Fact]
    public void WhenModifiersChangedThenIsCustomReturnsTrue()
    {
        HotkeyBinding binding = new HotkeyBinding(HotkeyAction.Save, "Save", Key.S, ModifierKeys.Control);

        binding.Modifiers = ModifierKeys.Alt;

        Assert.True(binding.IsCustom);
    }

    [Fact]
    public void WhenNotChangedThenIsCustomReturnsFalse()
    {
        HotkeyBinding binding = new HotkeyBinding(HotkeyAction.Save, "Save", Key.S, ModifierKeys.Control);

        Assert.False(binding.IsCustom);
    }

    [Fact]
    public void WhenResetToDefaultThenKeyAndModifiersRevert()
    {
        HotkeyBinding binding = new HotkeyBinding(HotkeyAction.Save, "Save", Key.S, ModifierKeys.Control);
        binding.Key = Key.W;
        binding.Modifiers = ModifierKeys.Alt;

        binding.ResetToDefault();

        Assert.Equal(Key.S, binding.Key);
        Assert.Equal(ModifierKeys.Control, binding.Modifiers);
        Assert.False(binding.IsCustom);
    }

    [Fact]
    public void WhenKeyChangedThenPropertyChangedIsRaised()
    {
        HotkeyBinding binding = new HotkeyBinding(HotkeyAction.Save, "Save", Key.S, ModifierKeys.Control);
        List<string?> changedProperties = new List<string?>();
        binding.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        binding.Key = Key.W;

        Assert.Contains("Key", changedProperties);
        Assert.Contains("GestureText", changedProperties);
        Assert.Contains("IsCustom", changedProperties);
    }

    [Fact]
    public void WhenModifiersChangedThenPropertyChangedIsRaised()
    {
        HotkeyBinding binding = new HotkeyBinding(HotkeyAction.Save, "Save", Key.S, ModifierKeys.Control);
        List<string?> changedProperties = new List<string?>();
        binding.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        binding.Modifiers = ModifierKeys.Alt;

        Assert.Contains("Modifiers", changedProperties);
        Assert.Contains("GestureText", changedProperties);
        Assert.Contains("IsCustom", changedProperties);
    }

    [Theory]
    [InlineData(ModifierKeys.Control, Key.S, "Ctrl+S")]
    [InlineData(ModifierKeys.Alt, Key.F4, "Alt+F4")]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift, Key.P, "Ctrl+Shift+P")]
    [InlineData(ModifierKeys.None, Key.F5, "F5")]
    public void WhenFormatGestureCalledThenReturnsExpectedText(ModifierKeys modifiers, Key key, string expected)
    {
        string result = HotkeyBinding.FormatGesture(modifiers, key);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenKeyIsNoneThenGestureTextIsEmpty()
    {
        string result = HotkeyBinding.FormatGesture(ModifierKeys.Control, Key.None);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void WhenGestureTextAccessedThenReturnsFormattedGesture()
    {
        HotkeyBinding binding = new HotkeyBinding(HotkeyAction.Save, "Save", Key.S, ModifierKeys.Control);

        Assert.Equal("Ctrl+S", binding.GestureText);
        Assert.Equal("Ctrl+S", binding.DefaultGestureText);
    }
}
