using KaneCode.Services;
using Microsoft.CodeAnalysis.Tags;
using System.Collections.Immutable;
using Xunit;

namespace KaneCode.Tests.Services;

public class RoslynCompletionProviderTests
{
    // --- GetSymbolKindIcon ---

    [Theory]
    [InlineData(WellKnownTags.Method, "🟣")]
    [InlineData(WellKnownTags.ExtensionMethod, "🟣")]
    [InlineData(WellKnownTags.Property, "🔧")]
    [InlineData(WellKnownTags.Field, "🔵")]
    [InlineData(WellKnownTags.Event, "⚡")]
    [InlineData(WellKnownTags.Class, "🟠")]
    [InlineData(WellKnownTags.Structure, "🟤")]
    [InlineData(WellKnownTags.Interface, "🔷")]
    [InlineData(WellKnownTags.Enum, "🟡")]
    [InlineData(WellKnownTags.EnumMember, "🟡")]
    [InlineData(WellKnownTags.Delegate, "🟣")]
    [InlineData(WellKnownTags.Namespace, "🔲")]
    [InlineData(WellKnownTags.Constant, "🔵")]
    [InlineData(WellKnownTags.Local, "📌")]
    [InlineData(WellKnownTags.Parameter, "📌")]
    [InlineData(WellKnownTags.RangeVariable, "📌")]
    [InlineData(WellKnownTags.TypeParameter, "🔶")]
    [InlineData(WellKnownTags.Keyword, "🔑")]
    [InlineData(WellKnownTags.Intrinsic, "🔑")]
    [InlineData(WellKnownTags.Snippet, "📋")]
    [InlineData(WellKnownTags.Label, "🏷️")]
    [InlineData(WellKnownTags.Operator, "➕")]
    public void WhenTagMatchesThenGetSymbolKindIconReturnsExpectedGlyph(string tag, string expectedIcon)
    {
        ImmutableArray<string> tags = [tag];
        string icon = RoslynCompletionProvider.GetSymbolKindIcon(tags);

        Assert.Equal(expectedIcon, icon);
    }

    [Fact]
    public void WhenNoRecognizedTagsThenGetSymbolKindIconReturnsDefault()
    {
        ImmutableArray<string> tags = ["SomeUnknownTag"];
        string icon = RoslynCompletionProvider.GetSymbolKindIcon(tags);

        Assert.Equal("⬜", icon);
    }

    [Fact]
    public void WhenEmptyTagsThenGetSymbolKindIconReturnsDefault()
    {
        ImmutableArray<string> tags = [];
        string icon = RoslynCompletionProvider.GetSymbolKindIcon(tags);

        Assert.Equal("⬜", icon);
    }

    [Fact]
    public void WhenMultipleTagsThenGetSymbolKindIconReturnsFirstMatch()
    {
        // Roslyn items often have both a symbol tag and an access modifier tag (e.g. "Method", "Public")
        ImmutableArray<string> tags = [WellKnownTags.Public, WellKnownTags.Method];
        string icon = RoslynCompletionProvider.GetSymbolKindIcon(tags);

        Assert.Equal("🟣", icon);
    }

    // --- ShouldAutoTrigger ---

    [Theory]
    [InlineData('.', true)]
    [InlineData('<', true)]
    [InlineData('[', true)]
    [InlineData(':', true)]
    [InlineData('_', true)]
    [InlineData('a', true)]
    [InlineData('Z', true)]
    [InlineData(' ', false)]
    [InlineData(';', false)]
    [InlineData(')', false)]
    [InlineData('1', false)]
    public void WhenCharTypedThenShouldAutoTriggerReturnsExpected(char typedChar, bool expected)
    {
        bool result = RoslynCompletionProvider.ShouldAutoTrigger(typedChar);

        Assert.Equal(expected, result);
    }

    // --- IsCommitCharacter ---

    [Theory]
    [InlineData(';', true)]
    [InlineData('(', true)]
    [InlineData(')', true)]
    [InlineData('[', true)]
    [InlineData(']', true)]
    [InlineData('{', true)]
    [InlineData('}', true)]
    [InlineData(' ', true)]
    [InlineData('.', true)]
    [InlineData(',', true)]
    [InlineData(':', true)]
    [InlineData('\t', true)]
    [InlineData('=', true)]
    [InlineData('<', true)]
    [InlineData('>', true)]
    [InlineData('a', false)]
    [InlineData('Z', false)]
    [InlineData('_', false)]
    [InlineData('5', false)]
    public void WhenCharCheckedThenIsCommitCharacterReturnsExpected(char ch, bool expected)
    {
        bool result = RoslynCompletionProvider.IsCommitCharacter(ch);

        Assert.Equal(expected, result);
    }
}
