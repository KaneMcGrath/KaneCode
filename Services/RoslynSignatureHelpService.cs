using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.ComponentModel;

namespace KaneCode.Services;

/// <summary>
/// Provides Roslyn-powered signature help (parameter info) for method calls.
/// Uses the semantic model and syntax tree to resolve method overloads and parameter info.
/// </summary>
internal sealed class RoslynSignatureHelpService
{
    private readonly RoslynWorkspaceService _roslynService;

    public RoslynSignatureHelpService(RoslynWorkspaceService roslynService)
    {
        ArgumentNullException.ThrowIfNull(roslynService);
        _roslynService = roslynService;
    }

    /// <summary>
    /// Gets signature help at the specified caret position.
    /// Returns null if no signature help is available.
    /// </summary>
    public async Task<SignatureHelpResult?> GetSignatureHelpAsync(
        string filePath,
        int position,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!RoslynWorkspaceService.IsCSharpFile(filePath))
        {
            return null;
        }

        var document = _roslynService.GetDocument(filePath);
        if (document is null)
        {
            return null;
        }

        var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (syntaxRoot is null || semanticModel is null)
        {
            return null;
        }

        // Walk up from the caret to find the enclosing argument list
        var token = syntaxRoot.FindToken(position);
        var argumentList = token.Parent?.AncestorsAndSelf().OfType<ArgumentListSyntax>().FirstOrDefault();
        if (argumentList?.Parent is not InvocationExpressionSyntax invocation)
        {
            // Try object creation: new Foo(|)
            var objectCreationArgList = token.Parent?.AncestorsAndSelf().OfType<ArgumentListSyntax>().FirstOrDefault();
            if (objectCreationArgList?.Parent is BaseObjectCreationExpressionSyntax creation)
            {
                return BuildFromObjectCreation(creation, objectCreationArgList, position, semanticModel, cancellationToken);
            }

            return null;
        }

        return BuildFromInvocation(invocation, argumentList, position, semanticModel, cancellationToken);
    }

    private static SignatureHelpResult? BuildFromInvocation(
        InvocationExpressionSyntax invocation,
        ArgumentListSyntax argumentList,
        int position,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(invocation.Expression, cancellationToken);

        var candidates = new List<IMethodSymbol>();

        if (symbolInfo.Symbol is IMethodSymbol directMethod)
        {
            candidates.Add(directMethod);
        }

        foreach (var candidate in symbolInfo.CandidateSymbols.OfType<IMethodSymbol>())
        {
            candidates.Add(candidate);
        }

        // If we found nothing from SymbolInfo, try the method group
        if (candidates.Count == 0)
        {
            var memberGroup = semanticModel.GetMemberGroup(invocation.Expression, cancellationToken);
            foreach (var member in memberGroup.OfType<IMethodSymbol>())
            {
                candidates.Add(member);
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var activeParam = GetActiveParameterIndex(argumentList, position);
        return BuildResult(candidates, activeParam, symbolInfo.Symbol as IMethodSymbol);
    }

    private static SignatureHelpResult? BuildFromObjectCreation(
        BaseObjectCreationExpressionSyntax creation,
        ArgumentListSyntax argumentList,
        int position,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(creation, cancellationToken);

        var candidates = new List<IMethodSymbol>();

        if (symbolInfo.Symbol is IMethodSymbol directCtor)
        {
            candidates.Add(directCtor);
        }

        foreach (var candidate in symbolInfo.CandidateSymbols.OfType<IMethodSymbol>())
        {
            candidates.Add(candidate);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var activeParam = GetActiveParameterIndex(argumentList, position);
        return BuildResult(candidates, activeParam, symbolInfo.Symbol as IMethodSymbol);
    }

    private static SignatureHelpResult BuildResult(
        List<IMethodSymbol> candidates,
        int activeParameterIndex,
        IMethodSymbol? bestMatch)
    {
        var overloads = new List<SignatureOverload>(candidates.Count);

        foreach (var method in candidates)
        {
            var header = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var documentation = method.GetDocumentationCommentXml() ?? string.Empty;
            var docSummary = ExtractDocSummary(documentation);

            var parameters = new List<SignatureParameter>(method.Parameters.Length);
            foreach (var param in method.Parameters)
            {
                var paramDisplay = $"{param.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {param.Name}";
                var paramDoc = ExtractParamDoc(documentation, param.Name);
                parameters.Add(new SignatureParameter(paramDisplay, paramDoc));
            }

            overloads.Add(new SignatureOverload(header, docSummary, parameters));
        }

        // Select the overload matching the resolved symbol, or default to first
        var selectedIndex = 0;
        if (bestMatch is not null)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                if (SymbolEqualityComparer.Default.Equals(candidates[i], bestMatch))
                {
                    selectedIndex = i;
                    break;
                }
            }
        }

        return new SignatureHelpResult(overloads, selectedIndex, activeParameterIndex);
    }

    /// <summary>
    /// Determines which parameter the caret is currently on by counting commas
    /// before the caret position within the argument list.
    /// </summary>
    private static int GetActiveParameterIndex(ArgumentListSyntax argumentList, int position)
    {
        var index = 0;
        foreach (var comma in argumentList.Arguments.GetSeparators())
        {
            if (comma.SpanStart >= position)
            {
                break;
            }

            index++;
        }

        return index;
    }

    /// <summary>
    /// Extracts the &lt;summary&gt; text from an XML documentation comment string.
    /// </summary>
    private static string ExtractDocSummary(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return string.Empty;
        }

        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var summary = doc.Descendants("summary").FirstOrDefault();
            return summary?.Value.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Extracts the &lt;param&gt; documentation for a specific parameter name.
    /// </summary>
    private static string ExtractParamDoc(string xml, string paramName)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return string.Empty;
        }

        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var paramElement = doc.Descendants("param")
                .FirstOrDefault(p => p.Attribute("name")?.Value == paramName);
            return paramElement?.Value.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

/// <summary>
/// Represents the complete signature help result for display.
/// </summary>
internal sealed record SignatureHelpResult(
    IReadOnlyList<SignatureOverload> Overloads,
    int SelectedOverloadIndex,
    int ActiveParameterIndex);

/// <summary>
/// Represents a single method overload in signature help.
/// </summary>
internal sealed record SignatureOverload(
    string Header,
    string Documentation,
    IReadOnlyList<SignatureParameter> Parameters);

/// <summary>
/// Represents a single parameter in a signature overload.
/// </summary>
internal sealed record SignatureParameter(string DisplayText, string Documentation);

/// <summary>
/// An <see cref="ICSharpCode.AvalonEdit.CodeCompletion.IOverloadProvider"/> implementation
/// that presents Roslyn signature help overloads in AvalonEdit's <see cref="ICSharpCode.AvalonEdit.CodeCompletion.OverloadInsightWindow"/>.
/// </summary>
internal sealed class SignatureHelpOverloadProvider : INotifyPropertyChanged, ICSharpCode.AvalonEdit.CodeCompletion.IOverloadProvider
{
    private readonly IReadOnlyList<SignatureOverload> _overloads;
    private readonly int _activeParameterIndex;
    private int _selectedIndex;

    public SignatureHelpOverloadProvider(SignatureHelpResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _overloads = result.Overloads;
        _activeParameterIndex = result.ActiveParameterIndex;
        _selectedIndex = result.SelectedOverloadIndex;
    }

    public int Count => _overloads.Count;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex != value)
            {
                _selectedIndex = value;
                OnPropertyChanged(nameof(SelectedIndex));
                OnPropertyChanged(nameof(CurrentHeader));
                OnPropertyChanged(nameof(CurrentContent));
                OnPropertyChanged(nameof(CurrentIndexText));
            }
        }
    }

    public string CurrentIndexText => $"{_selectedIndex + 1} of {_overloads.Count}";

    public object CurrentHeader
    {
        get
        {
            if (_selectedIndex < 0 || _selectedIndex >= _overloads.Count)
            {
                return string.Empty;
            }

            return _overloads[_selectedIndex].Header;
        }
    }

    public object CurrentContent
    {
        get
        {
            if (_selectedIndex < 0 || _selectedIndex >= _overloads.Count)
            {
                return string.Empty;
            }

            var overload = _overloads[_selectedIndex];
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(overload.Documentation))
            {
                parts.Add(overload.Documentation);
            }

            if (_activeParameterIndex >= 0 && _activeParameterIndex < overload.Parameters.Count)
            {
                var param = overload.Parameters[_activeParameterIndex];
                if (!string.IsNullOrWhiteSpace(param.Documentation))
                {
                    parts.Add($"**{param.DisplayText}**: {param.Documentation}");
                }
                else if (!string.IsNullOrWhiteSpace(param.DisplayText))
                {
                    parts.Add($"Parameter: {param.DisplayText}");
                }
            }

            return string.Join(Environment.NewLine, parts);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
