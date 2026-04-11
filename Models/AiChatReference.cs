using System.IO;

namespace KaneCode.Models;

/// <summary>
/// The type of reference attached to an AI chat conversation.
/// </summary>
internal enum AiReferenceKind
{
    /// <summary>An entire file.</summary>
    File,

    /// <summary>The current active document.</summary>
    CurrentDocument,

    /// <summary>All currently open editor documents.</summary>
    OpenDocuments,

    /// <summary>The current build output shown in the IDE.</summary>
    BuildOutput,

    /// <summary>A C# class declaration discovered in the loaded project.</summary>
    Class,

    /// <summary>An external folder attached for request-scoped tool access.</summary>
    ExternalFolder
}

/// <summary>
/// A reference to a code element attached to the current AI chat conversation.
/// The content is injected into the system/user context when sending messages.
/// </summary>
internal sealed class AiChatReference
{
    /// <summary>What kind of reference this is.</summary>
    public AiReferenceKind Kind { get; }

    /// <summary>Absolute path to the referenced file.</summary>
    public string FullPath { get; }

    /// <summary>Short display name shown in the reference tag.</summary>
    public string DisplayName { get; }

    /// <summary>The file content at the time the reference was added.</summary>
    public string Content { get; set; } = string.Empty;

    public AiChatReference(AiReferenceKind kind, string fullPath, string? displayName = null)
    {
        Kind = kind;
        FullPath = fullPath;
        DisplayName = !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : GetDefaultDisplayName(kind, fullPath);
    }

    /// <summary>
    /// Formats this reference for injection into the AI conversation context.
    /// </summary>
    public string ToContextString()
    {
        return Kind switch
        {
            AiReferenceKind.File => $"[File: {DisplayName}]\n```\n{Content}\n```",
            AiReferenceKind.CurrentDocument => $"[Current document: {DisplayName}]\nPath: {FullPath}\n```\n{Content}\n```",
            AiReferenceKind.OpenDocuments => $"[All open documents]\n{Content}",
            AiReferenceKind.BuildOutput => $"[Build output]\n```text\n{Content}\n```",
            AiReferenceKind.Class => $"[Class: {DisplayName}]\nPath: {FullPath}\n```csharp\n{Content}\n```",
            AiReferenceKind.ExternalFolder =>
                $"[External folder: {DisplayName}]\nPath: {FullPath}\nThis folder is external context outside the loaded project. For this request only, the agent may use read_file, list_files, and search_files with paths inside this folder.\nFiles:\n{Content}",
            _ => Content
        };
    }

    private static string GetDefaultDisplayName(AiReferenceKind kind, string fullPath)
    {
        return kind switch
        {
            AiReferenceKind.OpenDocuments => "All open documents",
            AiReferenceKind.BuildOutput => "Build output",
            AiReferenceKind.CurrentDocument => Path.GetFileName(fullPath),
            AiReferenceKind.ExternalFolder => GetFolderDisplayName(fullPath),
            _ => Path.GetFileName(fullPath)
        };
    }

    private static string GetFolderDisplayName(string fullPath)
    {
        string trimmedPath = Path.TrimEndingDirectorySeparator(fullPath);
        string displayName = Path.GetFileName(trimmedPath);

        return string.IsNullOrWhiteSpace(displayName)
            ? trimmedPath
            : displayName;
    }
}
