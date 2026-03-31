using KaneCode.Controls;
using KaneCode.Models;
using Microsoft.CodeAnalysis;

namespace KaneCode.Tests.Controls;

public class ErrorListPanelTests
{
    private static DiagnosticItem CreateItem(
        DiagnosticSeverity severity,
        string code = "CS0001",
        string message = "Test",
        string file = "Test.cs",
        string category = "Compiler",
        string project = "TestProject")
    {
        return new DiagnosticItem(severity, code, message, file, 1, 1, 0, 10, "/path/" + file, category, project);
    }

    // --- DiagnosticItem property tests ---

    [Fact]
    public void WhenSeverityIsErrorThenSeverityIconReturnsRedX()
    {
        DiagnosticItem item = CreateItem(DiagnosticSeverity.Error);

        Assert.Equal("\u274C", item.SeverityIcon);
    }

    [Fact]
    public void WhenSeverityIsWarningThenSeverityIconReturnsWarningSign()
    {
        DiagnosticItem item = CreateItem(DiagnosticSeverity.Warning);

        Assert.Equal("\u26A0", item.SeverityIcon);
    }

    [Fact]
    public void WhenSeverityIsInfoThenSeverityIconReturnsInfoSign()
    {
        DiagnosticItem item = CreateItem(DiagnosticSeverity.Info);

        Assert.Equal("\u2139", item.SeverityIcon);
    }

    [Fact]
    public void WhenSeverityIsHiddenThenSeverityIconReturnsQuestionMark()
    {
        DiagnosticItem item = CreateItem(DiagnosticSeverity.Hidden);

        Assert.Equal("\u2753", item.SeverityIcon);
    }

    [Fact]
    public void WhenCategoryIsSetThenSourceIncludesCodeAndCategory()
    {
        DiagnosticItem item = CreateItem(DiagnosticSeverity.Error, code: "CS0246", category: "Compiler");

        Assert.Equal("CS0246 (Compiler)", item.Source);
    }

    [Fact]
    public void WhenCategoryIsEmptyThenSourceReturnCodeOnly()
    {
        DiagnosticItem item = CreateItem(DiagnosticSeverity.Warning, code: "IDE0001", category: "");

        Assert.Equal("IDE0001", item.Source);
    }

    [Fact]
    public void WhenProjectIsSetThenProjectPropertyReturnsValue()
    {
        DiagnosticItem item = CreateItem(DiagnosticSeverity.Info, project: "MyProject");

        Assert.Equal("MyProject", item.Project);
    }

    // --- Filter logic tests (using static ShouldShowDiagnostic) ---

    [Fact]
    public void WhenShowErrorsTrueThenFilterAcceptsErrors()
    {
        DiagnosticItem item = CreateItem(DiagnosticSeverity.Error);

        Assert.True(ErrorListPanel.ShouldShowDiagnostic(item, showErrors: true, showWarnings: true, showMessages: true));
    }

    [Fact]
    public void WhenShowErrorsFalseThenFilterRejectsErrors()
    {
        DiagnosticItem item = CreateItem(DiagnosticSeverity.Error);

        Assert.False(ErrorListPanel.ShouldShowDiagnostic(item, showErrors: false, showWarnings: true, showMessages: true));
    }

    [Fact]
    public void WhenShowWarningsTrueThenFilterAcceptsWarnings()
    {
        DiagnosticItem item = CreateItem(DiagnosticSeverity.Warning);

        Assert.True(ErrorListPanel.ShouldShowDiagnostic(item, showErrors: true, showWarnings: true, showMessages: true));
    }

    [Fact]
    public void WhenShowWarningsFalseThenFilterRejectsWarnings()
    {
        DiagnosticItem item = CreateItem(DiagnosticSeverity.Warning);

        Assert.False(ErrorListPanel.ShouldShowDiagnostic(item, showErrors: true, showWarnings: false, showMessages: true));
    }

    [Fact]
    public void WhenShowMessagesTrueThenFilterAcceptsInfos()
    {
        DiagnosticItem item = CreateItem(DiagnosticSeverity.Info);

        Assert.True(ErrorListPanel.ShouldShowDiagnostic(item, showErrors: true, showWarnings: true, showMessages: true));
    }

    [Fact]
    public void WhenShowMessagesFalseThenFilterRejectsInfos()
    {
        DiagnosticItem item = CreateItem(DiagnosticSeverity.Info);

        Assert.False(ErrorListPanel.ShouldShowDiagnostic(item, showErrors: true, showWarnings: true, showMessages: false));
    }

    [Fact]
    public void WhenObjectIsNotDiagnosticItemThenFilterReturnsFalse()
    {
        Assert.False(ErrorListPanel.ShouldShowDiagnostic("not a diagnostic", true, true, true));
    }

    [Fact]
    public void WhenAllFiltersEnabledThenAllSeveritiesAccepted()
    {
        Assert.True(ErrorListPanel.ShouldShowDiagnostic(CreateItem(DiagnosticSeverity.Error), true, true, true));
        Assert.True(ErrorListPanel.ShouldShowDiagnostic(CreateItem(DiagnosticSeverity.Warning), true, true, true));
        Assert.True(ErrorListPanel.ShouldShowDiagnostic(CreateItem(DiagnosticSeverity.Info), true, true, true));
    }

    [Fact]
    public void WhenAllFiltersDisabledThenAllSeveritiesRejected()
    {
        Assert.False(ErrorListPanel.ShouldShowDiagnostic(CreateItem(DiagnosticSeverity.Error), false, false, false));
        Assert.False(ErrorListPanel.ShouldShowDiagnostic(CreateItem(DiagnosticSeverity.Warning), false, false, false));
        Assert.False(ErrorListPanel.ShouldShowDiagnostic(CreateItem(DiagnosticSeverity.Info), false, false, false));
    }

    [Fact]
    public void WhenOnlyErrorsEnabledThenOnlyErrorsPass()
    {
        Assert.True(ErrorListPanel.ShouldShowDiagnostic(CreateItem(DiagnosticSeverity.Error), true, false, false));
        Assert.False(ErrorListPanel.ShouldShowDiagnostic(CreateItem(DiagnosticSeverity.Warning), true, false, false));
        Assert.False(ErrorListPanel.ShouldShowDiagnostic(CreateItem(DiagnosticSeverity.Info), true, false, false));
    }

    [Fact]
    public void WhenObjectIsNullThenFilterReturnsFalse()
    {
        Assert.False(ErrorListPanel.ShouldShowDiagnostic(null!, true, true, true));
    }

    // --- Record equality / default value tests ---

    [Fact]
    public void WhenCategoryOmittedThenDefaultsToEmptyString()
    {
        DiagnosticItem item = new(DiagnosticSeverity.Error, "CS0001", "msg", "file.cs", 1, 1, 0, 5, "/path");

        Assert.Equal("", item.Category);
        Assert.Equal("", item.Project);
    }

    [Fact]
    public void WhenTwoItemsHaveSameValuesThenRecordEqualityHolds()
    {
        DiagnosticItem a = CreateItem(DiagnosticSeverity.Error, "CS0001", "msg", "file.cs", "Compiler", "Proj");
        DiagnosticItem b = CreateItem(DiagnosticSeverity.Error, "CS0001", "msg", "file.cs", "Compiler", "Proj");

        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(DiagnosticSeverity.Error)]
    [InlineData(DiagnosticSeverity.Warning)]
    [InlineData(DiagnosticSeverity.Info)]
    public void WhenSeverityVariesThenSeverityIconIsNotEmpty(DiagnosticSeverity severity)
    {
        DiagnosticItem item = CreateItem(severity);

        Assert.False(string.IsNullOrEmpty(item.SeverityIcon));
    }
}
