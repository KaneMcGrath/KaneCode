using KaneCode.Models;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KaneCode.Services.Ai;

/// <summary>
/// Central registry for all <see cref="IAgentTool"/> instances.
/// Provides tool lookup by name and serialization of tool definitions
/// for inclusion in OpenAI-compatible API requests.
/// </summary>
internal sealed class AgentToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools = new(StringComparer.Ordinal);

    /// <summary>All registered tools.</summary>
    public IReadOnlyCollection<IAgentTool> Tools => _tools.Values;

    /// <summary>
    /// Registers a tool. Replaces any existing tool with the same name.
    /// </summary>
    public void Register(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools[tool.Name] = tool;
    }

    /// <summary>
    /// Registers multiple tools at once.
    /// </summary>
    public void RegisterAll(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        foreach (var tool in tools)
        {
            Register(tool);
        }
    }

    /// <summary>
    /// Looks up a tool by name. Returns null if not found.
    /// </summary>
    public IAgentTool? Get(string name)
    {
        return _tools.TryGetValue(name, out var tool) ? tool : null;
    }

    /// <summary>
    /// Serializes all registered tools into the OpenAI-compatible
    /// <c>tools</c> array format for inclusion in a chat completion request body.
    /// Each entry has <c>type: "function"</c> and a <c>function</c> object
    /// with <c>name</c>, <c>description</c>, and <c>parameters</c>.
    /// </summary>
    /// <param name="allowedToolNames">
    /// When non-null, only tools whose name is in this set are serialized.
    /// </param>
    /// <param name="preset">
    /// Optional preset whose per-tool description overrides and pinned parameters
    /// are merged into the emitted definitions.
    /// </param>
    public JsonElement SerializeToolDefinitions(IEnumerable<string>? allowedToolNames = null, AiPreset? preset = null)
    {
        var toolsToSerialize = allowedToolNames != null
            ? _tools.Values.Where(t => allowedToolNames.Contains(t.Name)).ToList()
            : _tools.Values.ToList();

        if (toolsToSerialize.Count == 0)
        {
            return default;
        }

        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();

            foreach (var tool in toolsToSerialize)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "function");

                writer.WriteStartObject("function");
                writer.WriteString("name", tool.Name);
                writer.WriteString("description", ResolveDescription(tool, preset));
                writer.WritePropertyName("parameters");
                ResolveParametersSchema(tool, preset).WriteTo(writer);
                writer.WriteEndObject(); // function

                writer.WriteEndObject(); // tool entry
            }

            writer.WriteEndArray();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// Returns true if at least one tool is registered.
    /// </summary>
    public bool HasTools => _tools.Count > 0;

    /// <summary>
    /// Resolves the description sent to the model for a tool, applying the preset's
    /// description override (when set) and substituting pinned parameter values for
    /// <c>{param}</c> tokens. Tokens without a pinned value keep their name form.
    /// </summary>
    public string ResolveDescription(IAgentTool tool, AiPreset? preset)
    {
        ArgumentNullException.ThrowIfNull(tool);

        string description = preset?.ToolDescriptions is { } descriptions &&
                             descriptions.TryGetValue(tool.Name, out string? overrideDescription) &&
                             !string.IsNullOrWhiteSpace(overrideDescription)
            ? overrideDescription
            : tool.Description;

        if (preset?.PinnedParameters is { } pinned &&
            pinned.TryGetValue(tool.Name, out Dictionary<string, JsonElement>? parameterOverrides))
        {
            foreach ((string paramName, JsonElement value) in parameterOverrides)
            {
                string token = "{" + paramName + "}";
                if (description.Contains(token, StringComparison.Ordinal))
                {
                    description = description.Replace(token, JsonValueToText(value), StringComparison.Ordinal);
                }
            }
        }

        return description;
    }

    /// <summary>
    /// Resolves the parameters schema sent to the model for a tool. When the preset
    /// pins parameter values, a <c>default</c> is injected into each pinned property
    /// so the locked value is merged over the schema defaults.
    /// </summary>
    public JsonElement ResolveParametersSchema(IAgentTool tool, AiPreset? preset)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (preset?.PinnedParameters is not { } pinned ||
            !pinned.TryGetValue(tool.Name, out Dictionary<string, JsonElement>? parameterOverrides) ||
            parameterOverrides.Count == 0)
        {
            return tool.ParametersSchema;
        }

        if (tool.ParametersSchema.ValueKind != JsonValueKind.Object ||
            JsonNode.Parse(tool.ParametersSchema.GetRawText()) is not JsonObject schemaRoot)
        {
            return tool.ParametersSchema;
        }

        if (schemaRoot["properties"] is JsonObject properties)
        {
            foreach ((string paramName, JsonElement value) in parameterOverrides)
            {
                if (properties[paramName] is JsonObject property)
                {
                    property["default"] = JsonNode.Parse(value.GetRawText());
                }
            }
        }

        return JsonSerializer.SerializeToElement(schemaRoot);
    }

    /// <summary>
    /// Builds the full tool definition object (type + function { name, description,
    /// parameters }) with preset overrides applied. Used by the editor's
    /// "Tool definition" preview tab.
    /// </summary>
    public JsonObject BuildToolDefinition(IAgentTool tool, AiPreset? preset)
    {
        ArgumentNullException.ThrowIfNull(tool);

        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = ResolveDescription(tool, preset),
                ["parameters"] = JsonNode.Parse(ResolveParametersSchema(tool, preset).GetRawText())
            }
        };
    }

    /// <summary>
    /// Returns the names of parameters declared in a tool's schema, in declaration order.
    /// </summary>
    public static IReadOnlyList<string> GetParameterNames(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (tool.ParametersSchema.ValueKind == JsonValueKind.Object &&
            tool.ParametersSchema.TryGetProperty("properties", out JsonElement properties) &&
            properties.ValueKind == JsonValueKind.Object)
        {
            return properties.EnumerateObject().Select(p => p.Name).ToList();
        }

        return [];
    }

    /// <summary>
    /// Returns the names of parameters that are required per the tool's schema.
    /// </summary>
    public static IReadOnlyList<string> GetRequiredParameters(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (tool.ParametersSchema.ValueKind == JsonValueKind.Object &&
            tool.ParametersSchema.TryGetProperty("required", out JsonElement required) &&
            required.ValueKind == JsonValueKind.Array)
        {
            return required.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
        }

        return [];
    }

    /// <summary>
    /// Converts a JsonElement to its text form for substitution into descriptions.
    /// Strings are unquoted; other primitives use their raw JSON text.
    /// </summary>
    private static string JsonValueToText(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => value.GetRawText()
        };
    }
}
