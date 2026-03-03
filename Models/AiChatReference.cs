using System.IO;

namespace KaneCode.Models;

/// <summary>
/// The type of reference attached to an AI chat conversation.
/// </summary>
internal enum AiReferenceKind
{
    /// <summary>An entire file.</summary>
    File
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

    public AiChatReference(AiReferenceKind kind, string fullPath)
    {
        Kind = kind;
        FullPath = fullPath;
        DisplayName = Path.GetFileName(fullPath);
    }

    /// <summary>
    /// Formats this reference for injection into the AI conversation context.
    /// </summary>
    public string ToContextString()
    {
        return $"[File: {DisplayName}]\n```\n{Content}\n```";
    }
}
