using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace KaneCode.Services.Ai;

/// <summary>
/// AI provider that talks to a local Llama.cpp HTTP server using the
/// OpenAI-compatible <c>/v1/chat/completions</c> endpoint with SSE streaming.
/// </summary>
internal sealed class LlamaCppProvider : IAiProvider, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly AiProviderSettings _settings;

    public LlamaCppProvider(AiProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public string DisplayName => "Llama.cpp (local)";

    public string ProviderId => "llamacpp";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.Endpoint);

    public IReadOnlyList<string> AvailableModels =>
    [
        "default"
    ];

    /// <inheritdoc />
    public async IAsyncEnumerable<AiStreamToken> StreamCompletionAsync(
        IReadOnlyList<AiChatMessage> messages,
        string model,
        JsonElement tools = default,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var endpoint = _settings.Endpoint.TrimEnd('/');
        var url = $"{endpoint}/v1/chat/completions";

        var serializedMessages = SerializeMessages(messages);

        using var bodyStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bodyStream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", string.IsNullOrWhiteSpace(model) ? "default" : model);

            writer.WritePropertyName("messages");
            serializedMessages.WriteTo(writer);

            writer.WriteBoolean("stream", true);
            writer.WriteBoolean("cache_prompt", true);

            if (tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
            {
                writer.WritePropertyName("tools");
                tools.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(bodyStream.ToArray());
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Tool calls are streamed incrementally across multiple SSE chunks.
        // We accumulate them here and emit completed calls at the end.
        var pendingToolCalls = new Dictionary<int, (string? Id, string? Name, System.Text.StringBuilder Args)>();

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data: ".Length..];

            if (data is "[DONE]")
            {
                break;
            }

            await foreach (var token in ExtractStreamTokens(data, pendingToolCalls))
            {
                yield return token;
            }
        }

        // Emit any accumulated tool calls
        foreach (var (_, tc) in pendingToolCalls.OrderBy(kv => kv.Key))
        {
            if (!string.IsNullOrEmpty(tc.Name))
            {
                yield return new AiStreamToken(
                    AiStreamTokenType.ToolCall,
                    string.Empty,
                    ToolCall: new AiStreamToolCall(tc.Id ?? string.Empty, tc.Name, tc.Args.ToString()));
            }
        }
    }

    /// <summary>
    /// Serializes the message list into a JSON array, handling tool role and tool_calls fields.
    /// </summary>
    private static JsonElement SerializeMessages(IReadOnlyList<AiChatMessage> messages)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();

            foreach (var m in messages)
            {
                writer.WriteStartObject();

                var role = m.Role switch
                {
                    AiChatRole.System => "system",
                    AiChatRole.User => "user",
                    AiChatRole.Assistant => "assistant",
                    AiChatRole.Tool => "tool",
                    _ => "user"
                };
                writer.WriteString("role", role);
                writer.WriteString("content", m.Content);

                // Tool result messages need tool_call_id
                if (m.Role == AiChatRole.Tool && !string.IsNullOrEmpty(m.ToolCallId))
                {
                    writer.WriteString("tool_call_id", m.ToolCallId);
                }

                // Assistant messages with tool calls need the tool_calls array
                if (m.Role == AiChatRole.Assistant && m.ToolCalls is { Count: > 0 })
                {
                    writer.WriteStartArray("tool_calls");
                    foreach (var tc in m.ToolCalls)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", tc.Id);
                        writer.WriteString("type", "function");
                        writer.WriteStartObject("function");
                        writer.WriteString("name", tc.FunctionName);
                        writer.WriteString("arguments", tc.ArgumentsJson);
                        writer.WriteEndObject();
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// Extracts content, reasoning, and tool-call tokens from an SSE chat completion chunk.
    /// Tool calls arrive incrementally across multiple chunks — partial data is accumulated
    /// in <paramref name="pendingToolCalls"/> and emitted by the caller after the stream ends.
    /// </summary>
    private static IAsyncEnumerable<AiStreamToken> ExtractStreamTokens(
        string json,
        Dictionary<int, (string? Id, string? Name, System.Text.StringBuilder Args)> pendingToolCalls)
    {
        return ExtractStreamTokensCore(json, pendingToolCalls);

        static async IAsyncEnumerable<AiStreamToken> ExtractStreamTokensCore(
            string json,
            Dictionary<int, (string? Id, string? Name, System.Text.StringBuilder Args)> pendingToolCalls)
        {
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                yield break;
            }

            using (doc)
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var firstChoice = choices[0];
                    if (firstChoice.TryGetProperty("delta", out var delta))
                    {
                        // Reasoning tokens (chain-of-thought from reasoning models)
                        if (delta.TryGetProperty("reasoning_content", out var reasoning))
                        {
                            var text = reasoning.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                yield return new AiStreamToken(AiStreamTokenType.Reasoning, text);
                            }
                        }

                        // Normal content tokens
                        if (delta.TryGetProperty("content", out var content))
                        {
                            var text = content.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                yield return new AiStreamToken(AiStreamTokenType.Content, text);
                            }
                        }

                        // Incremental tool_calls
                        if (delta.TryGetProperty("tool_calls", out var toolCalls) &&
                            toolCalls.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tc in toolCalls.EnumerateArray())
                            {
                                var index = tc.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;

                                if (!pendingToolCalls.TryGetValue(index, out var entry))
                                {
                                    entry = (null, null, new System.Text.StringBuilder());
                                    pendingToolCalls[index] = entry;
                                }

                                if (tc.TryGetProperty("id", out var idProp))
                                {
                                    entry.Id = idProp.GetString();
                                    pendingToolCalls[index] = entry;
                                }

                                if (tc.TryGetProperty("function", out var fn))
                                {
                                    if (fn.TryGetProperty("name", out var name))
                                    {
                                        entry.Name = name.GetString();
                                        pendingToolCalls[index] = entry;
                                    }

                                    if (fn.TryGetProperty("arguments", out var args))
                                    {
                                        var argsText = args.GetString();
                                        if (!string.IsNullOrEmpty(argsText))
                                        {
                                            entry.Args.Append(argsText);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // Usage stats from the final chunk
                if (root.TryGetProperty("usage", out var usage) &&
                    usage.ValueKind == JsonValueKind.Object)
                {
                    var promptTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                    var completionTokens = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
                    var totalTokens = usage.TryGetProperty("total_tokens", out var tt) ? tt.GetInt32() : 0;

                    yield return new AiStreamToken(
                        AiStreamTokenType.Usage,
                        string.Empty,
                        new AiUsageStats(promptTokens, completionTokens, totalTokens));
                }
            }

            await Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
