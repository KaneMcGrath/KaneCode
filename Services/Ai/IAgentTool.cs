using System.Text.Json;

namespace KaneCode.Services.Ai;

/// <summary>
/// Represents a tool that an AI agent can invoke during a conversation.
/// Each tool declares its name, description, JSON Schema for parameters,
/// and an async execution method.
/// </summary>
internal interface IAgentTool
{
    /// <summary>
    /// Unique name used by the model to reference this tool (e.g. "read_file").
    /// Must match the OpenAI function-calling convention: lowercase with underscores.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Short human-readable description of what this tool does,
    /// included in the system prompt so the model knows when to call it.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// JSON Schema object describing the parameters this tool accepts.
    /// Serialized into the <c>tools[].function.parameters</c> field of the API request.
    /// </summary>
    JsonElement ParametersSchema { get; }

    /// <summary>
    /// Whether this tool performs a destructive/side-effecting action
    /// that should require user confirmation before execution.
    /// </summary>
    bool RequiresConfirmation => false;

    /// <summary>
    /// Executes the tool with the given arguments and returns a result.
    /// </summary>
    /// <param name="arguments">Parsed JSON arguments from the model's tool call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default);
}
