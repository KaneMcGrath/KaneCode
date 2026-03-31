using KaneCode.Services;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace KaneCode.Tests.Services;

public class RoslynSignatureHelpServiceTests
{
    // --- SelectBestOverloadIndex ---

    [Fact]
    public void WhenSingleOverloadThenSelectBestOverloadIndexReturnsZero()
    {
        List<SignatureOverload> overloads =
        [
            new SignatureOverload("Foo(int x)", "", [new SignatureParameter("int x", "")])
        ];

        int result = RoslynSignatureHelpService.SelectBestOverloadIndex(overloads, 0, 0);

        Assert.Equal(0, result);
    }

    [Fact]
    public void WhenExactMatchExistsThenSelectBestOverloadIndexPrefersIt()
    {
        List<SignatureOverload> overloads =
        [
            new SignatureOverload("Foo()", "", []),
            new SignatureOverload("Foo(int x)", "", [new SignatureParameter("int x", "")]),
            new SignatureOverload("Foo(int x, int y)", "", [new SignatureParameter("int x", ""), new SignatureParameter("int y", "")])
        ];

        // Active param index 1 means 2 arguments needed → exact match is overload with 2 params
        int result = RoslynSignatureHelpService.SelectBestOverloadIndex(overloads, 1, 0);

        Assert.Equal(2, result);
    }

    [Fact]
    public void WhenNoExactMatchThenSelectBestOverloadIndexPrefersSmallestSufficientOverload()
    {
        List<SignatureOverload> overloads =
        [
            new SignatureOverload("Foo(int x)", "", [new SignatureParameter("int x", "")]),
            new SignatureOverload("Foo(int x, int y, int z)", "", [new SignatureParameter("int x", ""), new SignatureParameter("int y", ""), new SignatureParameter("int z", "")])
        ];

        // Active param index 1 means 2 args needed. No exact 2-param overload.
        // Smallest with >= 2 params is overload[1] with 3 params.
        int result = RoslynSignatureHelpService.SelectBestOverloadIndex(overloads, 1, 0);

        Assert.Equal(1, result);
    }

    [Fact]
    public void WhenNoSufficientOverloadThenSelectBestOverloadIndexFallsBackToCompilerSelection()
    {
        List<SignatureOverload> overloads =
        [
            new SignatureOverload("Foo()", "", []),
            new SignatureOverload("Foo(int x)", "", [new SignatureParameter("int x", "")])
        ];

        // Active param index 2 means 3 args needed. No overload has >= 3 params.
        // Compiler selected index 1.
        int result = RoslynSignatureHelpService.SelectBestOverloadIndex(overloads, 2, 1);

        Assert.Equal(1, result);
    }

    [Fact]
    public void WhenCompilerIndexOutOfRangeThenSelectBestOverloadIndexClamps()
    {
        List<SignatureOverload> overloads =
        [
            new SignatureOverload("Foo()", "", []),
            new SignatureOverload("Foo(int x)", "", [new SignatureParameter("int x", "")])
        ];

        // Compiler selected index 5 (out of range). Active param 2 has no sufficient overload.
        int result = RoslynSignatureHelpService.SelectBestOverloadIndex(overloads, 2, 5);

        Assert.Equal(1, result);
    }

    [Fact]
    public void WhenEmptyOverloadsThenSelectBestOverloadIndexReturnsZero()
    {
        List<SignatureOverload> overloads = [];

        int result = RoslynSignatureHelpService.SelectBestOverloadIndex(overloads, 0, 0);

        Assert.Equal(0, result);
    }

    [Fact]
    public void WhenActiveParamZeroAndMultipleOverloadsThenSelectBestOverloadIndexPrefersOneParam()
    {
        List<SignatureOverload> overloads =
        [
            new SignatureOverload("Foo(int x, int y)", "", [new SignatureParameter("int x", ""), new SignatureParameter("int y", "")]),
            new SignatureOverload("Foo(int x)", "", [new SignatureParameter("int x", "")])
        ];

        // Active param 0 → need 1 param. Exact match is overload[1].
        int result = RoslynSignatureHelpService.SelectBestOverloadIndex(overloads, 0, 0);

        Assert.Equal(1, result);
    }

    // --- GetActiveParameterIndex ---

    [Fact]
    public void WhenCaretBeforeFirstArgThenGetActiveParameterIndexReturnsZero()
    {
        // Parse: Foo(x, y, z) — caret right after '('
        ArgumentListSyntax argList = ParseArgumentList("(x, y, z)");
        int openParenEnd = argList.OpenParenToken.Span.End;

        int result = RoslynSignatureHelpService.GetActiveParameterIndex(argList, openParenEnd);

        Assert.Equal(0, result);
    }

    [Fact]
    public void WhenCaretAfterFirstCommaThenGetActiveParameterIndexReturnsOne()
    {
        ArgumentListSyntax argList = ParseArgumentList("(x, y, z)");
        // Position after first comma
        var separators = argList.Arguments.GetSeparators().ToList();
        int afterFirstComma = separators[0].Span.End;

        int result = RoslynSignatureHelpService.GetActiveParameterIndex(argList, afterFirstComma);

        Assert.Equal(1, result);
    }

    [Fact]
    public void WhenCaretAfterSecondCommaThenGetActiveParameterIndexReturnsTwo()
    {
        ArgumentListSyntax argList = ParseArgumentList("(x, y, z)");
        var separators = argList.Arguments.GetSeparators().ToList();
        int afterSecondComma = separators[1].Span.End;

        int result = RoslynSignatureHelpService.GetActiveParameterIndex(argList, afterSecondComma);

        Assert.Equal(2, result);
    }

    [Fact]
    public void WhenEmptyArgListThenGetActiveParameterIndexReturnsZero()
    {
        ArgumentListSyntax argList = ParseArgumentList("()");
        int openParenEnd = argList.OpenParenToken.Span.End;

        int result = RoslynSignatureHelpService.GetActiveParameterIndex(argList, openParenEnd);

        Assert.Equal(0, result);
    }

    [Fact]
    public void WhenSingleArgThenGetActiveParameterIndexReturnsZero()
    {
        ArgumentListSyntax argList = ParseArgumentList("(x)");
        int openParenEnd = argList.OpenParenToken.Span.End;

        int result = RoslynSignatureHelpService.GetActiveParameterIndex(argList, openParenEnd);

        Assert.Equal(0, result);
    }

    // --- SignatureHelpResult / Records ---

    [Fact]
    public void WhenSignatureHelpResultCreatedThenPropertiesMatchInput()
    {
        List<SignatureParameter> parameters =
        [
            new SignatureParameter("int x", "The x value"),
            new SignatureParameter("string y", "The y value")
        ];
        List<SignatureOverload> overloads =
        [
            new SignatureOverload("Foo(int x, string y)", "Does foo", parameters)
        ];

        SignatureHelpResult result = new(overloads, 0, 1);

        Assert.Single(result.Overloads);
        Assert.Equal(0, result.SelectedOverloadIndex);
        Assert.Equal(1, result.ActiveParameterIndex);
        Assert.Equal("Foo(int x, string y)", result.Overloads[0].Header);
        Assert.Equal("Does foo", result.Overloads[0].Documentation);
        Assert.Equal(2, result.Overloads[0].Parameters.Count);
    }

    [Fact]
    public void WhenSignatureParameterCreatedThenPropertiesMatchInput()
    {
        SignatureParameter param = new("int x", "The x value");

        Assert.Equal("int x", param.DisplayText);
        Assert.Equal("The x value", param.Documentation);
    }

    // --- SignatureHelpOverloadProvider ---

    [Fact]
    public void WhenProviderCreatedThenCountMatchesOverloads()
    {
        SignatureHelpResult result = CreateSampleResult(overloadCount: 3, activeParam: 0, selectedOverload: 1);
        SignatureHelpOverloadProvider provider = new(result);

        Assert.Equal(3, provider.Count);
        Assert.Equal(1, provider.SelectedIndex);
        Assert.Equal(0, provider.ActiveParameterIndex);
    }

    [Fact]
    public void WhenSelectedIndexChangedThenPropertyChangedFires()
    {
        SignatureHelpResult result = CreateSampleResult(overloadCount: 2, activeParam: 0, selectedOverload: 0);
        SignatureHelpOverloadProvider provider = new(result);

        List<string> changedProperties = [];
        provider.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName!);

        provider.SelectedIndex = 1;

        Assert.Contains(nameof(provider.SelectedIndex), changedProperties);
        Assert.Contains(nameof(provider.CurrentHeader), changedProperties);
        Assert.Contains(nameof(provider.CurrentContent), changedProperties);
        Assert.Contains(nameof(provider.CurrentIndexText), changedProperties);
    }

    [Fact]
    public void WhenSelectedIndexSetToSameValueThenPropertyChangedDoesNotFire()
    {
        SignatureHelpResult result = CreateSampleResult(overloadCount: 2, activeParam: 0, selectedOverload: 0);
        SignatureHelpOverloadProvider provider = new(result);

        List<string> changedProperties = [];
        provider.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName!);

        provider.SelectedIndex = 0;

        Assert.Empty(changedProperties);
    }

    [Fact]
    public void WhenUpdateActiveParameterCalledThenHeaderAndContentRefresh()
    {
        SignatureHelpResult result = CreateSampleResult(overloadCount: 1, activeParam: 0, selectedOverload: 0);
        SignatureHelpOverloadProvider provider = new(result);

        List<string> changedProperties = [];
        provider.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName!);

        provider.UpdateActiveParameter(1);

        Assert.Equal(1, provider.ActiveParameterIndex);
        Assert.Contains(nameof(provider.CurrentHeader), changedProperties);
        Assert.Contains(nameof(provider.CurrentContent), changedProperties);
    }

    [Fact]
    public void WhenUpdateActiveParameterWithSameValueThenNoPropertyChanged()
    {
        SignatureHelpResult result = CreateSampleResult(overloadCount: 1, activeParam: 0, selectedOverload: 0);
        SignatureHelpOverloadProvider provider = new(result);

        List<string> changedProperties = [];
        provider.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName!);

        provider.UpdateActiveParameter(0);

        Assert.Empty(changedProperties);
    }

    [Fact]
    public void WhenUpdateResultCalledThenAllPropertiesRefresh()
    {
        SignatureHelpResult result1 = CreateSampleResult(overloadCount: 1, activeParam: 0, selectedOverload: 0);
        SignatureHelpOverloadProvider provider = new(result1);

        List<string> changedProperties = [];
        provider.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName!);

        SignatureHelpResult result2 = CreateSampleResult(overloadCount: 2, activeParam: 1, selectedOverload: 1);
        provider.UpdateResult(result2);

        Assert.Equal(2, provider.Count);
        Assert.Equal(1, provider.ActiveParameterIndex);
        Assert.Equal(1, provider.SelectedIndex);
        Assert.Contains(nameof(provider.Count), changedProperties);
        Assert.Contains(nameof(provider.SelectedIndex), changedProperties);
        Assert.Contains(nameof(provider.CurrentIndexText), changedProperties);
        Assert.Contains(nameof(provider.CurrentHeader), changedProperties);
        Assert.Contains(nameof(provider.CurrentContent), changedProperties);
    }

    [Fact]
    public void WhenCurrentIndexTextQueriedThenReturnsFormattedString()
    {
        SignatureHelpResult result = CreateSampleResult(overloadCount: 3, activeParam: 0, selectedOverload: 1);
        SignatureHelpOverloadProvider provider = new(result);

        Assert.Equal("2 of 3", provider.CurrentIndexText);
    }

    [Fact]
    public void WhenSelectedIndexOutOfRangeThenCurrentHeaderReturnsEmpty()
    {
        SignatureHelpResult result = CreateSampleResult(overloadCount: 1, activeParam: 0, selectedOverload: 0);
        SignatureHelpOverloadProvider provider = new(result);

        // Force an out-of-range index
        provider.SelectedIndex = -1;

        Assert.Equal(string.Empty, provider.CurrentHeader);
    }

    [Fact]
    public void WhenSelectedIndexOutOfRangeThenCurrentContentReturnsEmpty()
    {
        SignatureHelpResult result = CreateSampleResult(overloadCount: 1, activeParam: 0, selectedOverload: 0);
        SignatureHelpOverloadProvider provider = new(result);

        provider.SelectedIndex = -1;

        Assert.Equal(string.Empty, provider.CurrentContent);
    }

    // --- Helpers ---

    private static ArgumentListSyntax ParseArgumentList(string argumentListText)
    {
        // Wrap in a valid invocation expression to parse
        string code = $"M{argumentListText}";
        ExpressionSyntax expression = SyntaxFactory.ParseExpression(code);
        InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)expression;
        return invocation.ArgumentList;
    }

    private static SignatureHelpResult CreateSampleResult(int overloadCount, int activeParam, int selectedOverload)
    {
        List<SignatureOverload> overloads = [];
        for (int i = 0; i < overloadCount; i++)
        {
            List<SignatureParameter> parameters =
            [
                new SignatureParameter($"int arg{i}_0", $"Doc for arg{i}_0"),
                new SignatureParameter($"string arg{i}_1", $"Doc for arg{i}_1")
            ];
            overloads.Add(new SignatureOverload($"Method{i}(int arg{i}_0, string arg{i}_1)", $"Docs for Method{i}", parameters));
        }

        return new SignatureHelpResult(overloads, selectedOverload, activeParam);
    }
}
