using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using KaneCode.Theming;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Tags;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KaneCode.Services;

/// <summary>
/// Provides Roslyn-powered code completion for the AvalonEdit editor.
/// </summary>
internal sealed class RoslynCompletionProvider
{
    private readonly RoslynWorkspaceService _roslynService;
    private static bool _importCompletionConfigured;

    public RoslynCompletionProvider(RoslynWorkspaceService roslynService)
    {
        ArgumentNullException.ThrowIfNull(roslynService);
        _roslynService = roslynService;
    }

    /// <summary>
    /// Gets completion items at the specified caret offset after ensuring the workspace text is up-to-date.
    /// </summary>
    public async Task<CompletionResult?> GetCompletionsAsync(
        string filePath,
        string currentText,
        int caretOffset,
        CancellationToken cancellationToken = default)
    {
        if (!RoslynWorkspaceService.IsCSharpFile(filePath))
        {
            return null;
        }

        // Push the latest editor text into Roslyn so completions reflect the current state
        await _roslynService.UpdateDocumentTextAsync(filePath, currentText, cancellationToken).ConfigureAwait(false);

        var document = _roslynService.GetDocument(filePath);
        if (document is null)
        {
            return null;
        }

        var completionService = CompletionService.GetService(document);
        if (completionService is null)
        {
            return null;
        }

        // Try to enable import completion on first use
        TryEnableImportCompletion(document.Project.Solution.Workspace);

        var completionList = await completionService.GetCompletionsAsync(
            document, caretOffset, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (completionList is null || completionList.ItemsList.Count == 0)
        {
            return null;
        }

        var results = new List<RoslynCompletionData>(completionList.ItemsList.Count);
        foreach (var item in completionList.ItemsList)
        {
            results.Add(new RoslynCompletionData(item, document, completionService));
        }

        // Roslyn tells us the span it considers the "filter text" for the completion.
        // Use its start as the CompletionWindow.StartOffset so AvalonEdit filters as you type.
        var defaultSpanStart = completionList.Span.Start;

        return new CompletionResult(results, defaultSpanStart);
    }

    /// <summary>
    /// Attempts to enable import completion (showing types from unreferenced namespaces)
    /// by configuring Roslyn's internal global option service via reflection.
    /// This is best-effort; if the internal API changes, completion still works without imports.
    /// </summary>
    private static void TryEnableImportCompletion(Workspace workspace)
    {
        if (_importCompletionConfigured)
        {
            return;
        }

        _importCompletionConfigured = true;

        try
        {
            // Roslyn's IGlobalOptionService is internal but exported via MEF.
            // We resolve it from the workspace's services and set the import-completion options.
            Type? globalOptionServiceType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return []; }
                })
                .FirstOrDefault(t => t.Name == "IGlobalOptionService" && t.IsInterface);

            if (globalOptionServiceType is null)
            {
                return;
            }

            // Get the service from the workspace's MEF host services
            object? globalOptionService = workspace.Services.GetType()
                .GetMethod("GetService")
                ?.MakeGenericMethod(globalOptionServiceType)
                .Invoke(workspace.Services, null);

            if (globalOptionService is null)
            {
                return;
            }

            // Find the SetGlobalOption method
            MethodInfo? setOptionMethod = globalOptionServiceType.GetMethod("SetGlobalOption");
            if (setOptionMethod is null)
            {
                return;
            }

            // Find OptionKey2 type
            Type? optionKey2Type = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return []; }
                })
                .FirstOrDefault(t => t.FullName == "Microsoft.CodeAnalysis.Options.OptionKey2");

            if (optionKey2Type is null)
            {
                return;
            }

            // Find the ShowItemsFromUnimportedNamespaces option definition
            Type? completionOptionsStorageType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return []; }
                })
                .FirstOrDefault(t => t.Name == "CompletionOptionsStorage");

            if (completionOptionsStorageType is null)
            {
                return;
            }

            FieldInfo? optionField = completionOptionsStorageType.GetField(
                "ShowItemsFromUnimportedNamespaces",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            if (optionField is null)
            {
                return;
            }

            object? optionDefinition = optionField.GetValue(null);
            if (optionDefinition is null)
            {
                return;
            }

            // Create an OptionKey2 from the option definition for C# language
            object? optionKey = Activator.CreateInstance(optionKey2Type, optionDefinition, LanguageNames.CSharp);
            if (optionKey is null)
            {
                return;
            }

            // Set the option value to true
            setOptionMethod.Invoke(globalOptionService, [optionKey, true]);

            // Also enable the expanded completion index for import completion
            FieldInfo? indexField = completionOptionsStorageType.GetField(
                "ForceExpandedCompletionIndexCreation",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            if (indexField is not null)
            {
                object? indexOption = indexField.GetValue(null);
                if (indexOption is not null)
                {
                    object? indexKey = Activator.CreateInstance(optionKey2Type, indexOption, LanguageNames.CSharp);
                    if (indexKey is not null)
                    {
                        setOptionMethod.Invoke(globalOptionService, [indexKey, true]);
                    }
                }
            }

            Debug.WriteLine("Import completion enabled via IGlobalOptionService.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not enable import completion: {ex.Message}");
        }
    }

    /// <summary>
    /// Determines whether completion should be auto-triggered for the given character.
    /// </summary>
    public static bool ShouldAutoTrigger(char typedChar)
    {
        return typedChar == '.'
            || typedChar == '<'
            || typedChar == '['
            || typedChar == ':'
            || char.IsLetter(typedChar)
            || typedChar == '_';
    }

    /// <summary>
    /// Determines whether the given character should commit (accept) the selected completion item.
    /// Standard IDE commit characters include punctuation that terminates an identifier context.
    /// </summary>
    internal static bool IsCommitCharacter(char ch)
    {
        return ch is ';' or '(' or ')' or '[' or ']' or '{' or '}' or ' ' or '.' or ',' or ':' or '+' or '-' or '*' or '/' or '%' or '&' or '|' or '^' or '!' or '~' or '=' or '<' or '>' or '?' or '#' or '\t' or '\n' or '\r';
    }

    /// <summary>
    /// Returns an emoji glyph representing the symbol kind based on the Roslyn completion item's tags.
    /// </summary>
    internal static string GetSymbolKindIcon(ImmutableArray<string> tags)
    {
        foreach (string tag in tags)
        {
            string? icon = tag switch
            {
                WellKnownTags.Method or WellKnownTags.ExtensionMethod => "🟣",
                WellKnownTags.Property => "🔧",
                WellKnownTags.Field => "🔵",
                WellKnownTags.Event => "⚡",
                WellKnownTags.Class => "🟠",
                WellKnownTags.Structure => "🟤",
                WellKnownTags.Interface => "🔷",
                WellKnownTags.Enum => "🟡",
                WellKnownTags.EnumMember => "🟡",
                WellKnownTags.Delegate => "🟣",
                WellKnownTags.Namespace => "🔲",
                WellKnownTags.Constant => "🔵",
                WellKnownTags.Local or WellKnownTags.Parameter or WellKnownTags.RangeVariable => "📌",
                WellKnownTags.TypeParameter => "🔶",
                WellKnownTags.Keyword or WellKnownTags.Intrinsic => "🔑",
                WellKnownTags.Snippet => "📋",
                WellKnownTags.Label => "🏷️",
                WellKnownTags.Operator => "➕",
                _ => null
            };

            if (icon is not null)
            {
                return icon;
            }
        }

        return "⬜";
    }

    /// <summary>
    /// Applies theme colors to a <see cref="CompletionWindow"/> so text is readable in dark themes.
    /// </summary>
    public static void ApplyTheme(CompletionWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var bgBrush = Application.Current.TryFindResource(ThemeResourceKeys.CompletionBackground) as Brush;
        var fgBrush = Application.Current.TryFindResource(ThemeResourceKeys.CompletionForeground) as Brush;

        if (bgBrush is not null)
        {
            window.Background = bgBrush;
        }

        if (fgBrush is not null)
        {
            window.Foreground = fgBrush;
        }

        if (Application.Current.TryFindResource(ThemeResourceKeys.CompletionBorder) is Brush borderBrush)
        {
            window.BorderBrush = borderBrush;
            window.BorderThickness = new Thickness(1);
        }

        // Style the inner ListBox so items inherit the foreground color
        var listBox = FindCompletionListBox(window);
        if (listBox is not null)
        {
            listBox.Foreground = fgBrush ?? SystemColors.ControlTextBrush;
            listBox.Background = bgBrush ?? SystemColors.WindowBrush;
        }
    }

    /// <summary>
    /// Walks the visual/logical tree of the CompletionWindow to find the inner ListBox.
    /// </summary>
    private static ListBox? FindCompletionListBox(CompletionWindow window)
    {
        // CompletionWindow.CompletionList is a CompletionList control that contains a ListBox
        var completionList = window.CompletionList;
        if (completionList is null)
        {
            return null;
        }

        return completionList.ListBox;
    }
}

/// <summary>
/// Result of a completion query, containing items and the span start for filtering.
/// </summary>
internal sealed record CompletionResult(IReadOnlyList<RoslynCompletionData> Items, int SpanStart);

/// <summary>
/// An AvalonEdit completion data item backed by a Roslyn <see cref="CompletionItem"/>.
/// Description is pre-fetched asynchronously; completion change is fetched on demand at insertion time.
/// </summary>
internal sealed class RoslynCompletionData : ICompletionData
{
    private readonly Microsoft.CodeAnalysis.Completion.CompletionItem _roslynItem;
    private readonly Document _document;
    private readonly CompletionService _completionService;

    private readonly Task<string> _descriptionTask;
    private readonly string _iconGlyph;

    public RoslynCompletionData(
        Microsoft.CodeAnalysis.Completion.CompletionItem roslynItem,
        Document document,
        CompletionService completionService)
    {
        _roslynItem = roslynItem;
        _document = document;
        _completionService = completionService;

        _descriptionTask = FetchDescriptionAsync();
        _iconGlyph = RoslynCompletionProvider.GetSymbolKindIcon(roslynItem.Tags);
    }

    /// <summary>
    /// The Roslyn <see cref="CompletionItem"/> backing this entry. Exposed for import-completion handling.
    /// </summary>
    internal Microsoft.CodeAnalysis.Completion.CompletionItem RoslynItem => _roslynItem;

    public ImageSource? Image => null;

    public string Text => _roslynItem.DisplayText;

    public object Content
    {
        get
        {
            string displayText = _roslynItem.DisplayTextPrefix + _roslynItem.DisplayText + _roslynItem.DisplayTextSuffix;

            // Show namespace suffix for import-completion items
            if (_roslynItem.Properties.TryGetValue("NamespaceName", out string? ns) && !string.IsNullOrEmpty(ns))
            {
                displayText += $"  ({ns})";
            }

            StackPanel panel = new()
            {
                Orientation = Orientation.Horizontal
            };

            panel.Children.Add(new TextBlock
            {
                Text = _iconGlyph,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12
            });

            panel.Children.Add(new TextBlock
            {
                Text = displayText,
                VerticalAlignment = VerticalAlignment.Center
            });

            return panel;
        }
    }

    public object Description
    {
        get
        {
            if (!_descriptionTask.IsCompletedSuccessfully || string.IsNullOrWhiteSpace(_descriptionTask.Result))
            {
                return _roslynItem.DisplayText;
            }

            // Return a themed tooltip panel with the description
            Border border = new()
            {
                Padding = new Thickness(6, 4, 6, 4),
                MaxWidth = 500
            };

            if (Application.Current.TryFindResource(ThemeResourceKeys.TooltipBackground) is Brush bgBrush)
            {
                border.Background = bgBrush;
            }

            TextBlock textBlock = new()
            {
                Text = _descriptionTask.Result,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 480
            };

            if (Application.Current.TryFindResource(ThemeResourceKeys.TooltipForeground) is Brush fgBrush)
            {
                textBlock.Foreground = fgBrush;
            }

            border.Child = textBlock;
            return border;
        }
    }

    public double Priority => _roslynItem.Rules.MatchPriority;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        try
        {
            // Fetch the completion change to get the intended replacement text (may include
            // extras like parentheses or generic parameters beyond the display text).
            // For import-completion items, this also returns the using directive change.
            string insertText = Text;
            try
            {
                var change = _completionService.GetChangeAsync(_document, _roslynItem).GetAwaiter().GetResult();
                if (change?.TextChanges is { Length: > 0 } textChanges)
                {
                    // The last text change is the main insertion; earlier changes are using directives.
                    Microsoft.CodeAnalysis.Text.TextChange mainChange = textChanges[^1];
                    insertText = mainChange.NewText;

                    // Apply using directive additions (import-completion) before the main text
                    if (textChanges.Length > 1)
                    {
                        var sourceText = _document.GetTextAsync().GetAwaiter().GetResult();
                        // Apply changes in reverse order to avoid offset shifting
                        for (int i = textChanges.Length - 2; i >= 0; i--)
                        {
                            Microsoft.CodeAnalysis.Text.TextChange additionalChange = textChanges[i];
                            int avalonStart = additionalChange.Span.Start;
                            int avalonLength = additionalChange.Span.Length;
                            string newText = additionalChange.NewText ?? string.Empty;

                            textArea.Document.Replace(avalonStart, avalonLength, newText);
                        }

                        // Adjust the completion segment for any offset changes from preceding edits
                        int offsetDelta = 0;
                        for (int i = 0; i < textChanges.Length - 1; i++)
                        {
                            Microsoft.CodeAnalysis.Text.TextChange tc = textChanges[i];
                            offsetDelta += (tc.NewText?.Length ?? 0) - tc.Span.Length;
                        }

                        int adjustedStart = completionSegment.Offset + offsetDelta;
                        int adjustedLength = completionSegment.Length;
                        textArea.Document.Replace(adjustedStart, adjustedLength, insertText);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to fetch completion change: {ex.Message}");
            }

            // Standard single-change insertion
            textArea.Document.Replace(completionSegment, insertText);
        }
        catch
        {
            textArea.Document.Replace(completionSegment, Text);
        }
    }

    private async Task<string> FetchDescriptionAsync()
    {
        try
        {
            var description = await _completionService.GetDescriptionAsync(_document, _roslynItem).ConfigureAwait(false);
            return description?.Text ?? string.Empty;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to fetch completion description: {ex.Message}");
            return string.Empty;
        }
    }
}
