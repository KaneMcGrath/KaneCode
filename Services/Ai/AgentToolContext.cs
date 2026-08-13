using KaneCode.Models;
using System.Text.Json;

namespace KaneCode.Services.Ai;

/// <summary>
/// Carries the effective backend options for the currently executing tool.
///
/// Backend options are per-preset configuration that control how a tool
/// <em>executes</em> (engine choice, matching behavior, safety knobs). They are
/// resolved by merging a tool's <see cref="IAgentTool.DefaultBackendOptions"/> with
/// the active preset's <see cref="AiPreset.ToolOptions"/> overrides, then pushed
/// into an <see cref="AsyncLocal{T}"/> slot for the duration of a single
/// <see cref="IAgentTool.ExecuteAsync"/> call. Tools that opt in read their
/// effective values through the typed getters on this class.
/// </summary>
internal static class AgentToolContext
{
    private static readonly AsyncLocal<IReadOnlyDictionary<string, JsonElement>?> CurrentOptions = new();

    /// <summary>
    /// When non-null, overrides the project root that path-aware agent tools resolve
    /// against. The ticket system sets this to a ticket's worktree root while an agent
    /// runs so its file/build tools operate in isolation from the user's workspace.
    /// Flows through the async execution context, so every tool call an agent makes
    /// during a run observes the override.
    /// </summary>
    private static readonly AsyncLocal<string?> CurrentProjectRootOverride = new();

    /// <summary>
    /// When non-null, identifies the ticket the currently executing agent run is
    /// working on. Read by the ticket status tools (<c>complete_ticket</c> and
    /// <c>unable_to_complete</c>) so they update the correct ticket.
    /// </summary>
    private static readonly AsyncLocal<string?> CurrentTicketId = new();

    /// <summary>
    /// The effective backend options for the tool currently executing, or null
    /// when no context was pushed (e.g. direct tool invocation in tests).
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement>? Current => CurrentOptions.Value;

    /// <summary>
    /// Pushes a backend-options scope for the duration of the returned
    /// <see cref="IDisposable"/>. Nested scopes restore the previous value on dispose.
    /// </summary>
    public static IDisposable Push(IReadOnlyDictionary<string, JsonElement>? options)
    {
        return new Scope(options);
    }

    /// <summary>
    /// Pushes an agent-run scope carrying the ticket's project-root override and
    /// ticket id for the duration of the returned <see cref="IDisposable"/>.
    /// Nested scopes restore the previous values on dispose.
    /// </summary>
    public static IDisposable PushRunContext(string? projectRootOverride, string? ticketId)
    {
        return new RunContextScope(projectRootOverride, ticketId);
    }

    /// <summary>
    /// Returns the active project-root override, or null when agent tools should
    /// resolve against the loaded project as usual.
    /// </summary>
    public static string? GetProjectRootOverride()
    {
        return CurrentProjectRootOverride.Value;
    }

    /// <summary>
    /// Returns the ticket id for the currently executing agent run, or null when the
    /// tool is not executing inside a ticket agent run.
    /// </summary>
    public static string? GetCurrentTicketId()
    {
        return CurrentTicketId.Value;
    }

    /// <summary>
    /// Merges a tool's defaults with the given preset's per-tool overrides.
    /// Preset values win per key. The result is a snapshot that can be safely
    /// pushed into <see cref="Push"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement> Resolve(IAgentTool tool, AiPreset? preset)
    {
        ArgumentNullException.ThrowIfNull(tool);

        Dictionary<string, JsonElement> effective = new(StringComparer.Ordinal);
        foreach ((string key, JsonElement value) in tool.DefaultBackendOptions)
        {
            effective[key] = value.Clone();
        }

        if (preset?.ToolOptions is { } toolOptions &&
            toolOptions.TryGetValue(tool.Name, out Dictionary<string, JsonElement>? overrides))
        {
            foreach ((string key, JsonElement value) in overrides)
            {
                effective[key] = value.Clone();
            }
        }

        return effective;
    }

    /// <summary>Returns the string value of the named option, or <paramref name="fallback"/>.</summary>
    public static string GetString(string key, string fallback)
    {
        if (Current is { } options &&
            options.TryGetValue(key, out JsonElement element) &&
            element.ValueKind == JsonValueKind.String &&
            element.GetString() is { } value &&
            !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return fallback;
    }

    /// <summary>Returns the boolean value of the named option, or <paramref name="fallback"/>.</summary>
    public static bool GetBool(string key, bool fallback)
    {
        if (Current is { } options && options.TryGetValue(key, out JsonElement element))
        {
            if (element.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }

        return fallback;
    }

    /// <summary>Returns the integer value of the named option, or <paramref name="fallback"/>.</summary>
    public static int GetInt(string key, int fallback)
    {
        if (Current is { } options &&
            options.TryGetValue(key, out JsonElement element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out int value))
        {
            return value;
        }

        return fallback;
    }

    private sealed class Scope : IDisposable
    {
        private readonly IReadOnlyDictionary<string, JsonElement>? _previous;

        public Scope(IReadOnlyDictionary<string, JsonElement>? options)
        {
            _previous = CurrentOptions.Value;
            CurrentOptions.Value = options;
        }

        public void Dispose()
        {
            CurrentOptions.Value = _previous;
        }
    }

    private sealed class RunContextScope : IDisposable
    {
        private readonly string? _previousProjectRootOverride;
        private readonly string? _previousTicketId;

        public RunContextScope(string? projectRootOverride, string? ticketId)
        {
            _previousProjectRootOverride = CurrentProjectRootOverride.Value;
            _previousTicketId = CurrentTicketId.Value;
            CurrentProjectRootOverride.Value = projectRootOverride;
            CurrentTicketId.Value = ticketId;
        }

        public void Dispose()
        {
            CurrentProjectRootOverride.Value = _previousProjectRootOverride;
            CurrentTicketId.Value = _previousTicketId;
        }
    }
}
