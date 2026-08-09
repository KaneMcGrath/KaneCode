namespace KaneCode.Models;

/// <summary>
/// Represents a named source template persisted in the user template store.
/// </summary>
public sealed record FileTemplate
{
    public required string Name { get; init; }

    public required string Body { get; init; }
}
