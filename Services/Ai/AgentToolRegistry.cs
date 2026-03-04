using System.Text.Json;

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
    public JsonElement SerializeToolDefinitions()
    {
        if (_tools.Count == 0)
        {
            return default;
        }

        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();

            foreach (var tool in _tools.Values)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "function");

                writer.WriteStartObject("function");
                writer.WriteString("name", tool.Name);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("parameters");
                tool.ParametersSchema.WriteTo(writer);
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
}
