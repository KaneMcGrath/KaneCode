using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

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

        // Select the overload matching the resolved symbol, or fall back to best match by parameter count
        var compilerSelectedIndex = 0;
        if (bestMatch is not null)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                if (SymbolEqualityComparer.Default.Equals(candidates[i], bestMatch))
                {
                    compilerSelectedIndex = i;
                    break;
                }
            }
        }

        var selectedIndex = SelectBestOverloadIndex(overloads, activeParameterIndex, compilerSelectedIndex);

        return new SignatureHelpResult(overloads, selectedIndex, activeParameterIndex);
    }

    /// <summary>
    /// Checks whether the caret is inside an argument list and returns the updated
    /// active parameter index. Returns null if the caret is outside any argument list.
    /// </summary>
    public async Task<SignatureHelpResult?> GetUpdatedSignatureHelpAsync(
        string filePath,
        int position,
        CancellationToken cancellationToken = default)
    {
        // Reuses GetSignatureHelpAsync which re-resolves from the current syntax/semantic state.
        // This enables both parameter index updates and overload re-selection on every caret move.
        return await GetSignatureHelpAsync(filePath, position, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines whether the given caret position is inside an argument list.
    /// </summary>
    public async Task<bool> IsInsideArgumentListAsync(
        string filePath,
        int position,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!RoslynWorkspaceService.IsCSharpFile(filePath))
        {
            return false;
        }

        var document = _roslynService.GetDocument(filePath);
        if (document is null)
        {
            return false;
        }

        var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (syntaxRoot is null)
        {
            return false;
        }

        var token = syntaxRoot.FindToken(position);
        var argumentList = token.Parent?.AncestorsAndSelf().OfType<ArgumentListSyntax>().FirstOrDefault();
        if (argumentList is null)
        {
            return false;
        }

        // Verify the caret is within the parentheses (after '(' and before ')')
        return position > argumentList.OpenParenToken.SpanStart
               && position <= argumentList.CloseParenToken.SpanStart;
    }

    /// <summary>
    /// Selects the overload that best matches the current argument count.
    /// Prefers exact match, then the closest overload with at least that many parameters,
    /// then falls back to the compiler's best match.
    /// </summary>
    internal static int SelectBestOverloadIndex(
        IReadOnlyList<SignatureOverload> overloads,
        int activeParameterIndex,
        int compilerSelectedIndex)
    {
        if (overloads.Count <= 1)
        {
            return 0;
        }

        int requiredParamCount = activeParameterIndex + 1;

        // First: look for an exact parameter count match
        for (int i = 0; i < overloads.Count; i++)
        {
            if (overloads[i].Parameters.Count == requiredParamCount)
            {
                return i;
            }
        }

        // Second: find the overload with the smallest parameter count >= required
        int bestIndex = -1;
        int bestCount = int.MaxValue;
        for (int i = 0; i < overloads.Count; i++)
        {
            int paramCount = overloads[i].Parameters.Count;
            if (paramCount >= requiredParamCount && paramCount < bestCount)
            {
                bestCount = paramCount;
                bestIndex = i;
            }
        }

        if (bestIndex >= 0)
        {
            return bestIndex;
        }

        // Fall back to compiler's selection
        return Math.Clamp(compilerSelectedIndex, 0, overloads.Count - 1);
    }

    /// <summary>
    /// Determines which parameter the caret is currently on by counting commas
    /// before the caret position within the argument list.
    /// </summary>
    internal static int GetActiveParameterIndex(ArgumentListSyntax argumentList, int position)
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
/// Supports mutable active parameter index and rich WPF formatting.
/// </summary>
internal sealed class SignatureHelpOverloadProvider : INotifyPropertyChanged, ICSharpCode.AvalonEdit.CodeCompletion.IOverloadProvider
{
    private IReadOnlyList<SignatureOverload> _overloads;
    private int _activeParameterIndex;
    private int _selectedIndex;

    public SignatureHelpOverloadProvider(SignatureHelpResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _overloads = result.Overloads;
        _activeParameterIndex = result.ActiveParameterIndex;
        _selectedIndex = result.SelectedOverloadIndex;
    }

    public int Count => _overloads.Count;

    /// <summary>
    /// Gets the current active parameter index (zero-based).
    /// </summary>
    public int ActiveParameterIndex => _activeParameterIndex;

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

    /// <summary>
    /// Returns a WPF TextBlock with the method signature where the active parameter is bold.
    /// </summary>
    public object CurrentHeader
    {
        get
        {
            if (_selectedIndex < 0 || _selectedIndex >= _overloads.Count)
            {
                return string.Empty;
            }

            var overload = _overloads[_selectedIndex];
            return BuildFormattedHeader(overload, _activeParameterIndex);
        }
    }

    /// <summary>
    /// Returns a WPF TextBlock with the method documentation and active parameter info.
    /// </summary>
    public object CurrentContent
    {
        get
        {
            if (_selectedIndex < 0 || _selectedIndex >= _overloads.Count)
            {
                return string.Empty;
            }

            var overload = _overloads[_selectedIndex];
            return BuildFormattedContent(overload, _activeParameterIndex);
        }
    }

    /// <summary>
    /// Updates the active parameter index and refreshes the displayed content.
    /// Called when the caret moves within the argument list.
    /// </summary>
    public void UpdateActiveParameter(int newActiveParameterIndex)
    {
        if (_activeParameterIndex == newActiveParameterIndex)
        {
            return;
        }

        _activeParameterIndex = newActiveParameterIndex;
        OnPropertyChanged(nameof(CurrentHeader));
        OnPropertyChanged(nameof(CurrentContent));
    }

    /// <summary>
    /// Replaces the entire result (overloads + selection + active param) and refreshes all bindings.
    /// Used when a full re-query is needed (e.g. after argument types change).
    /// </summary>
    public void UpdateResult(SignatureHelpResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _overloads = result.Overloads;
        _activeParameterIndex = result.ActiveParameterIndex;
        _selectedIndex = result.SelectedOverloadIndex;
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(CurrentIndexText));
        OnPropertyChanged(nameof(CurrentHeader));
        OnPropertyChanged(nameof(CurrentContent));
    }

    /// <summary>
    /// Builds a WPF TextBlock with the method name, parentheses, and parameters.
    /// The active parameter is rendered in bold with an accent color.
    /// </summary>
    internal static object BuildFormattedHeader(SignatureOverload overload, int activeParameterIndex)
    {
        var textBlock = new TextBlock { TextWrapping = TextWrapping.Wrap };

        // Parse the header to extract the method name and parameter list
        string header = overload.Header;
        int openParenIndex = header.IndexOf('(');
        if (openParenIndex < 0 || overload.Parameters.Count == 0)
        {
            // No parameters or can't parse — return plain text
            textBlock.Inlines.Add(new Run(header));
            return textBlock;
        }

        // Method name portion (everything before the '(')
        string methodPart = header[..(openParenIndex + 1)];
        textBlock.Inlines.Add(new Run(methodPart));

        // Build parameter runs with the active one highlighted
        for (int i = 0; i < overload.Parameters.Count; i++)
        {
            if (i > 0)
            {
                textBlock.Inlines.Add(new Run(", "));
            }

            var paramRun = new Run(overload.Parameters[i].DisplayText);
            if (i == activeParameterIndex)
            {
                paramRun.FontWeight = FontWeights.Bold;
                paramRun.Foreground = GetActiveParameterBrush();
            }

            textBlock.Inlines.Add(paramRun);
        }

        textBlock.Inlines.Add(new Run(")"));
        return textBlock;
    }

    /// <summary>
    /// Builds a WPF TextBlock with the method documentation and current parameter info.
    /// </summary>
    internal static object BuildFormattedContent(SignatureOverload overload, int activeParameterIndex)
    {
        var textBlock = new TextBlock { TextWrapping = TextWrapping.Wrap };
        bool hasContent = false;

        if (!string.IsNullOrWhiteSpace(overload.Documentation))
        {
            textBlock.Inlines.Add(new Run(overload.Documentation) { FontStyle = FontStyles.Italic });
            hasContent = true;
        }

        if (activeParameterIndex >= 0 && activeParameterIndex < overload.Parameters.Count)
        {
            var param = overload.Parameters[activeParameterIndex];
            if (!string.IsNullOrWhiteSpace(param.Documentation) || !string.IsNullOrWhiteSpace(param.DisplayText))
            {
                if (hasContent)
                {
                    textBlock.Inlines.Add(new LineBreak());
                }

                var paramNameRun = new Run(param.DisplayText)
                {
                    FontWeight = FontWeights.Bold,
                    Foreground = GetActiveParameterBrush()
                };
                textBlock.Inlines.Add(paramNameRun);

                if (!string.IsNullOrWhiteSpace(param.Documentation))
                {
                    textBlock.Inlines.Add(new Run($": {param.Documentation}"));
                }

                hasContent = true;
            }
        }

        if (!hasContent)
        {
            return string.Empty;
        }

        return textBlock;
    }

    private static Brush GetActiveParameterBrush()
    {
        if (Application.Current?.TryFindResource(Theming.ThemeResourceKeys.SyntaxKeywordForeground) is Brush keywordBrush)
        {
            return keywordBrush;
        }

        // Default highlight color when no theme is available
        return Brushes.DodgerBlue;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
