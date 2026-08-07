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
    /// Unique name used by the model to reference this tool (e.g. "read").
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
    /// The logical category/group this tool belongs to (e.g. "Read Files",
    /// "Write Files", "Dotnet", "Presentation"). Used to group tools in the
    /// tools dropdown UI.
    /// </summary>
    string Category => "General";

    /// <summary>
    /// Whether this tool performs a destructive/side-effecting action
    /// that should require user confirmation before execution.
    /// </summary>
    bool RequiresConfirmation => false;

    /// <summary>
    /// JSON Schema describing user-editable backend options for this tool
    /// (engine choice, engine-specific knobs, safety settings). Backend options
    /// control how the tool <em>executes</em> and are never sent to the model.
    /// Empty/absent (<see cref="JsonValueKind.Undefined"/>) means the tool has
    /// no configurable backend options.
    /// </summary>
    /// <remarks>
    /// The schema follows JSON Schema conventions with a few extensions:
    /// <list type="bullet">
    /// <item>The <c>engine</c> property (when present) is an <c>enum</c> whose values
    /// drive the "Implementation" card. Per-value metadata may be supplied via an
    /// <c>x-enum-descriptions</c> object (value -&gt; description) and an
    /// <c>x-enum-recommended</c> array listing recommended values.</item>
    /// <item>Properties may carry an <c>engines</c> array; such options are only shown
    /// when the selected engine is a member of the array.</item>
    /// <item>Properties may carry a <c>group</c> string ("matching", "execution", …)
    /// used to group option cards in the editor.</item>
    /// </list>
    /// </remarks>
    JsonElement BackendOptionsSchema => default;

    /// <summary>
    /// Default backend option values before any preset override. Only options that
    /// differ from these defaults are stored per preset.
    /// </summary>
    IReadOnlyDictionary<string, JsonElement> DefaultBackendOptions => new Dictionary<string, JsonElement>();

    /// <summary>
    /// Executes the tool with the given arguments and returns a result.
    /// Tools that declare <see cref="BackendOptionsSchema"/> read their effective
    /// configuration from <see cref="AgentToolContext"/> (pushed by the execution
    /// layer) so per-preset options apply without changing the tool's code path.
    /// </summary>
    /// <param name="arguments">Parsed JSON arguments from the model's tool call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default);
}
