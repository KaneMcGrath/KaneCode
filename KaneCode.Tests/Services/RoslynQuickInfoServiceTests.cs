using KaneCode.Services;
using Microsoft.CodeAnalysis;
using Xunit;

namespace KaneCode.Tests.Services;

public class RoslynQuickInfoServiceTests
{
    // --- FindOverlappingDiagnostics ---

    [Fact]
    public void WhenPositionInsideDiagnosticSpanThenReturnsMatchingEntry()
    {
        List<DiagnosticEntry> entries =
        [
            new DiagnosticEntry(10, 20, DiagnosticSeverity.Error, "Error here", "CS0001"),
            new DiagnosticEntry(30, 40, DiagnosticSeverity.Warning, "Warning here", "CS0002")
        ];

        IReadOnlyList<DiagnosticEntry> result = RoslynQuickInfoService.FindOverlappingDiagnostics(entries, 15);

        Assert.Single(result);
        Assert.Equal("CS0001", result[0].Id);
    }

    [Fact]
    public void WhenPositionOutsideAllSpansThenReturnsEmpty()
    {
        List<DiagnosticEntry> entries =
        [
            new DiagnosticEntry(10, 20, DiagnosticSeverity.Error, "Error", "CS0001"),
            new DiagnosticEntry(30, 40, DiagnosticSeverity.Warning, "Warning", "CS0002")
        ];

        IReadOnlyList<DiagnosticEntry> result = RoslynQuickInfoService.FindOverlappingDiagnostics(entries, 25);

        Assert.Empty(result);
    }

    [Fact]
    public void WhenPositionAtStartOfSpanThenReturnsMatchingEntry()
    {
        List<DiagnosticEntry> entries =
        [
            new DiagnosticEntry(10, 20, DiagnosticSeverity.Error, "Error", "CS0001")
        ];

        IReadOnlyList<DiagnosticEntry> result = RoslynQuickInfoService.FindOverlappingDiagnostics(entries, 10);

        Assert.Single(result);
    }

    [Fact]
    public void WhenPositionAtEndOfSpanThenReturnsMatchingEntry()
    {
        List<DiagnosticEntry> entries =
        [
            new DiagnosticEntry(10, 20, DiagnosticSeverity.Error, "Error", "CS0001")
        ];

        IReadOnlyList<DiagnosticEntry> result = RoslynQuickInfoService.FindOverlappingDiagnostics(entries, 20);

        Assert.Single(result);
    }

    [Fact]
    public void WhenMultipleSpansOverlapThenReturnsAll()
    {
        List<DiagnosticEntry> entries =
        [
            new DiagnosticEntry(5, 25, DiagnosticSeverity.Error, "Error", "CS0001"),
            new DiagnosticEntry(10, 30, DiagnosticSeverity.Warning, "Warning", "CS0002"),
            new DiagnosticEntry(50, 60, DiagnosticSeverity.Info, "Info", "CS0003")
        ];

        IReadOnlyList<DiagnosticEntry> result = RoslynQuickInfoService.FindOverlappingDiagnostics(entries, 15);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void WhenDiagnosticsNullThenReturnsEmpty()
    {
        IReadOnlyList<DiagnosticEntry> result = RoslynQuickInfoService.FindOverlappingDiagnostics(null, 15);

        Assert.Empty(result);
    }

    [Fact]
    public void WhenDiagnosticsEmptyThenReturnsEmpty()
    {
        IReadOnlyList<DiagnosticEntry> result = RoslynQuickInfoService.FindOverlappingDiagnostics([], 15);

        Assert.Empty(result);
    }

    // --- GetThemeKeyForTag ---

    [Theory]
    [InlineData("Keyword", "SyntaxKeywordForeground")]
    [InlineData("Class", "RoslynTypeForeground")]
    [InlineData("Record", "RoslynTypeForeground")]
    [InlineData("RecordStruct", "RoslynTypeForeground")]
    [InlineData("Struct", "RoslynTypeForeground")]
    [InlineData("Interface", "RoslynInterfaceForeground")]
    [InlineData("Enum", "RoslynEnumForeground")]
    [InlineData("EnumMember", "RoslynEnumMemberForeground")]
    [InlineData("Delegate", "RoslynDelegateForeground")]
    [InlineData("TypeParameter", "RoslynTypeParameterForeground")]
    [InlineData("Method", "RoslynMethodForeground")]
    [InlineData("ExtensionMethod", "RoslynMethodForeground")]
    [InlineData("Property", "RoslynPropertyForeground")]
    [InlineData("Event", "RoslynEventForeground")]
    [InlineData("Field", "RoslynFieldForeground")]
    [InlineData("Constant", "RoslynFieldForeground")]
    [InlineData("Parameter", "RoslynParameterForeground")]
    [InlineData("Local", "RoslynLocalForeground")]
    [InlineData("RangeVariable", "RoslynLocalForeground")]
    [InlineData("Namespace", "RoslynNamespaceForeground")]
    [InlineData("StringLiteral", "SyntaxStringForeground")]
    [InlineData("NumericLiteral", "SyntaxNumberForeground")]
    [InlineData("Operator", "RoslynOperatorOverloadForeground")]
    [InlineData("Label", "RoslynLabelForeground")]
    public void WhenKnownTagThenGetThemeKeyForTagReturnsExpectedKey(string tag, string expectedKey)
    {
        string? result = RoslynQuickInfoService.GetThemeKeyForTag(tag);

        Assert.Equal(expectedKey, result);
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("Space")]
    [InlineData("Punctuation")]
    [InlineData("LineBreak")]
    [InlineData("UnknownTag")]
    public void WhenUnmappedTagThenGetThemeKeyForTagReturnsNull(string tag)
    {
        string? result = RoslynQuickInfoService.GetThemeKeyForTag(tag);

        Assert.Null(result);
    }

    // --- BuildQuickInfoSections ---

    [Fact]
    public void WhenQuickInfoIsNullThenBuildQuickInfoSectionsReturnsEmpty()
    {
        IReadOnlyList<QuickInfoSection> result = RoslynQuickInfoService.BuildQuickInfoSections(null);

        Assert.Empty(result);
    }

    // --- QuickInfoResult.ToPlainText ---

    [Fact]
    public void WhenResultHasSectionsAndDiagnosticsThenToPlainTextIncludesAll()
    {
        List<QuickInfoTaggedText> taggedParts =
        [
            new QuickInfoTaggedText("Keyword", "void"),
            new QuickInfoTaggedText("Space", " "),
            new QuickInfoTaggedText("Method", "MyMethod"),
            new QuickInfoTaggedText("Punctuation", "()")
        ];

        List<QuickInfoSection> sections =
        [
            new QuickInfoSection("Description", taggedParts)
        ];

        List<DiagnosticEntry> diagnostics =
        [
            new DiagnosticEntry(0, 10, DiagnosticSeverity.Error, "Something is wrong", "CS0001")
        ];

        QuickInfoResult result = new(sections, diagnostics, 0, 10);
        string plainText = result.ToPlainText();

        Assert.Contains("void MyMethod()", plainText);
        Assert.Contains("CS0001", plainText);
        Assert.Contains("Something is wrong", plainText);
    }

    [Fact]
    public void WhenResultHasOnlySectionsThenToPlainTextExcludesDiagnostics()
    {
        List<QuickInfoTaggedText> taggedParts =
        [
            new QuickInfoTaggedText("Keyword", "int"),
            new QuickInfoTaggedText("Space", " "),
            new QuickInfoTaggedText("Field", "myField")
        ];

        List<QuickInfoSection> sections =
        [
            new QuickInfoSection("Description", taggedParts)
        ];

        QuickInfoResult result = new(sections, [], 0, 5);
        string plainText = result.ToPlainText();

        Assert.Equal("int myField", plainText);
    }

    [Fact]
    public void WhenResultHasOnlyDiagnosticsThenToPlainTextShowsDiagnostics()
    {
        List<DiagnosticEntry> diagnostics =
        [
            new DiagnosticEntry(0, 10, DiagnosticSeverity.Warning, "Unused variable", "CS0168")
        ];

        QuickInfoResult result = new([], diagnostics, 0, 10);
        string plainText = result.ToPlainText();

        Assert.Contains("Warning", plainText);
        Assert.Contains("CS0168", plainText);
        Assert.Contains("Unused variable", plainText);
    }

    [Fact]
    public void WhenResultEmptyThenToPlainTextReturnsEmpty()
    {
        QuickInfoResult result = new([], [], 0, 0);
        string plainText = result.ToPlainText();

        Assert.Equal(string.Empty, plainText);
    }

    // --- QuickInfoTaggedText record ---

    [Fact]
    public void WhenQuickInfoTaggedTextCreatedThenPropertiesMatchInput()
    {
        QuickInfoTaggedText tagged = new("Keyword", "void");

        Assert.Equal("Keyword", tagged.Tag);
        Assert.Equal("void", tagged.Text);
    }

    // --- QuickInfoSection record ---

    [Fact]
    public void WhenQuickInfoSectionCreatedThenPropertiesMatchInput()
    {
        List<QuickInfoTaggedText> parts =
        [
            new QuickInfoTaggedText("Text", "hello")
        ];

        QuickInfoSection section = new("Description", parts);

        Assert.Equal("Description", section.Kind);
        Assert.Single(section.TaggedParts);
    }

    // --- DiagnosticEntry record (basic) ---

    [Fact]
    public void WhenDiagnosticEntryCreatedThenPropertiesMatchInput()
    {
        DiagnosticEntry entry = new(5, 15, DiagnosticSeverity.Error, "Bad code", "CS1234");

        Assert.Equal(5, entry.Start);
        Assert.Equal(15, entry.End);
        Assert.Equal(DiagnosticSeverity.Error, entry.Severity);
        Assert.Equal("Bad code", entry.Message);
        Assert.Equal("CS1234", entry.Id);
    }
}
