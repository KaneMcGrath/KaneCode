using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace KaneCode.Services.Ai;

/// <summary>
/// AI provider that talks to an OpenAI-compatible <c>/v1/chat/completions</c>
/// endpoint with optional SSE streaming and an optional API key.
/// </summary>
internal sealed class OpenAiProvider : IAiProvider, IDisposable
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly AiProviderSettings _settings;
    private IReadOnlyList<string> _availableModels;

    public OpenAiProvider(AiProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _availableModels = GetFallbackModels(settings.SelectedModel);
    }

    public string DisplayName => string.IsNullOrWhiteSpace(_settings.Label)
        ? "OpenAI-compatible"
        : _settings.Label;

    public string ProviderId => "openai";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.Endpoint);

    public IReadOnlyList<string> AvailableModels => _availableModels;

    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return _availableModels;
        }

        try
        {
            string url = BuildModelsUrl(_settings.Endpoint);
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return _availableModels;
            }

            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _availableModels = ParseAvailableModelsResponse(responseBody, _settings.SelectedModel);
        }
        catch (HttpRequestException)
        {
            Debug.WriteLine("AI model discovery request failed.");
        }
        catch (InvalidOperationException)
        {
            Debug.WriteLine("AI model discovery could not build a valid models endpoint.");
        }
        catch (JsonException)
        {
            Debug.WriteLine("AI model discovery returned an unexpected JSON payload.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Debug.WriteLine("AI model discovery timed out.");
        }

        return _availableModels;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AiStreamToken> StreamCompletionAsync(
        IReadOnlyList<AiChatMessage> messages,
        string model,
        JsonElement tools = default,
        bool streamResponse = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        string url = BuildChatCompletionsUrl(_settings.Endpoint);
        string resolvedModel = ResolveModel(model, _settings.SelectedModel);
        JsonElement serializedMessages = SerializeMessages(messages);
        bool isGroqCompatibleEndpoint = IsGroqCompatibleEndpoint(_settings.Endpoint);
        string json = BuildChatCompletionRequestJson(
            resolvedModel,
            serializedMessages,
            tools,
            _settings,
            isGroqCompatibleEndpoint,
            streamResponse);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            streamResponse ? "text/event-stream" : "application/json"));

        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        }

        using var response = await _httpClient.SendAsync(
            request,
            streamResponse ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"AI request failed with {(int)response.StatusCode} ({response.ReasonPhrase}). {errorBody}".Trim());
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            foreach (var token in ExtractCompletionTokens(responseJson))
            {
                yield return token;
            }

            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var pendingToolCalls = new Dictionary<int, (string? Id, string? Name, StringBuilder Args)>();

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!TryExtractEventData(line, out var data))
            {
                continue;
            }

            if (data is "[DONE]")
            {
                break;
            }

            await foreach (var token in ExtractStreamTokens(data, pendingToolCalls).ConfigureAwait(false))
            {
                yield return token;
            }
        }
    }

    internal static string BuildChatCompletionRequestJson(
        string model,
        JsonElement serializedMessages,
        JsonElement tools,
        AiProviderSettings settings,
        bool isGroqCompatibleEndpoint,
        bool streamResponse)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(settings);

        using MemoryStream bodyStream = new MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(bodyStream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);

            writer.WritePropertyName("messages");
            serializedMessages.WriteTo(writer);

            writer.WriteBoolean("stream", streamResponse);

            if (tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
            {
                writer.WritePropertyName("tools");
                tools.WriteTo(writer);
            }

            WriteInferenceParameters(writer, settings, isGroqCompatibleEndpoint);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(bodyStream.ToArray());
    }

    private static string ResolveModel(string requestedModel, string configuredModel)
    {
        if (!string.IsNullOrWhiteSpace(requestedModel))
        {
            return requestedModel;
        }

        if (!string.IsNullOrWhiteSpace(configuredModel))
        {
            return configuredModel;
        }

        return "default";
    }

    private static void WriteInferenceParameters(Utf8JsonWriter writer, AiProviderSettings settings, bool isGroqCompatibleEndpoint)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Temperature.HasValue)
        {
            writer.WriteNumber("temperature", settings.Temperature.Value);
        }

        if (settings.TopP.HasValue)
        {
            writer.WriteNumber("top_p", settings.TopP.Value);
        }

        if (!isGroqCompatibleEndpoint && settings.TopK.HasValue)
        {
            writer.WriteNumber("top_k", settings.TopK.Value);
        }

        if (!isGroqCompatibleEndpoint && settings.MinP.HasValue)
        {
            writer.WriteNumber("min_p", settings.MinP.Value);
        }

        if (!isGroqCompatibleEndpoint && settings.PresencePenalty.HasValue)
        {
            writer.WriteNumber("presence_penalty", settings.PresencePenalty.Value);
        }

        if (!isGroqCompatibleEndpoint && settings.RepetitionPenalty.HasValue)
        {
            writer.WriteNumber("repetition_penalty", settings.RepetitionPenalty.Value);
        }
    }

    private static bool IsGroqCompatibleEndpoint(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return uri.Host.EndsWith("groq.com", StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildModelsUrl(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidOperationException("The configured AI endpoint must be an absolute URL.");
        }

        string path = uri.AbsolutePath.TrimEnd('/');
        string targetPath = path switch
        {
            "" or "/" => "/v1/models",
            _ when path.EndsWith("/v1/models", StringComparison.OrdinalIgnoreCase) => path,
            _ when path.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase) =>
                string.Concat(path.AsSpan(0, path.Length - "/chat/completions".Length), "/models"),
            _ when path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) =>
                string.Concat(path.AsSpan(0, path.Length - "/chat/completions".Length), "/models"),
            _ when path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) => $"{path}/models",
            _ => $"{path}/v1/models"
        };

        UriBuilder builder = new(uri)
        {
            Path = targetPath
        };

        return builder.Uri.ToString();
    }

    private static string BuildChatCompletionsUrl(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("The configured AI endpoint must be an absolute URL.");
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        var targetPath = path switch
        {
            "" or "/" => "/v1/chat/completions",
            _ when path.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase) => path,
            _ when path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) => path,
            _ when path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) => $"{path}/chat/completions",
            _ => $"{path}/v1/chat/completions"
        };

        var builder = new UriBuilder(uri)
        {
            Path = targetPath
        };

        return builder.Uri.ToString();
    }

    internal static IReadOnlyList<string> ParseAvailableModelsResponse(string json, string? selectedModel = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        using JsonDocument document = JsonDocument.Parse(json);
        List<string> models = [];
        HashSet<string> seenModels = new(StringComparer.OrdinalIgnoreCase);

        if (document.RootElement.TryGetProperty("data", out JsonElement dataElement) &&
            dataElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement modelElement in dataElement.EnumerateArray())
            {
                if (!modelElement.TryGetProperty("id", out JsonElement idElement))
                {
                    continue;
                }

                string? modelId = idElement.GetString();
                if (string.IsNullOrWhiteSpace(modelId) || !seenModels.Add(modelId))
                {
                    continue;
                }

                models.Add(modelId);
            }
        }

        return MergeAvailableModels(models, selectedModel);
    }

    private static IReadOnlyList<string> MergeAvailableModels(IReadOnlyList<string> discoveredModels, string? selectedModel)
    {
        ArgumentNullException.ThrowIfNull(discoveredModels);

        List<string> mergedModels = [];
        HashSet<string> seenModels = new(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(selectedModel) && seenModels.Add(selectedModel))
        {
            mergedModels.Add(selectedModel);
        }

        foreach (string discoveredModel in discoveredModels)
        {
            if (string.IsNullOrWhiteSpace(discoveredModel) || !seenModels.Add(discoveredModel))
            {
                continue;
            }

            mergedModels.Add(discoveredModel);
        }

        return mergedModels.Count > 0 ? mergedModels : ["default"];
    }

    private static IReadOnlyList<string> GetFallbackModels(string? selectedModel)
    {
        return string.IsNullOrWhiteSpace(selectedModel)
            ? ["default"]
            : [selectedModel];
    }

    private static bool TryExtractEventData(string line, out string data)
    {
        const string Prefix = "data:";
        if (!line.StartsWith(Prefix, StringComparison.Ordinal))
        {
            data = string.Empty;
            return false;
        }

        data = line[Prefix.Length..].TrimStart();
        return true;
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

                if (m.Role == AiChatRole.Tool && !string.IsNullOrEmpty(m.ToolCallId))
                {
                    writer.WriteString("tool_call_id", m.ToolCallId);
                }

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
    /// </summary>
    private static IAsyncEnumerable<AiStreamToken> ExtractStreamTokens(
        string json,
        Dictionary<int, (string? Id, string? Name, StringBuilder Args)> pendingToolCalls)
    {
        return ExtractStreamTokensCore(json, pendingToolCalls);

        static async IAsyncEnumerable<AiStreamToken> ExtractStreamTokensCore(
            string json,
            Dictionary<int, (string? Id, string? Name, StringBuilder Args)> pendingToolCalls)
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
                foreach (var token in ExtractTokens(doc.RootElement, pendingToolCalls))
                {
                    yield return token;
                }
            }

            await Task.CompletedTask;
        }
    }

    private static IEnumerable<AiStreamToken> ExtractCompletionTokens(string json)
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
            foreach (var token in ExtractTokens(doc.RootElement, pendingToolCalls: null))
            {
                yield return token;
            }
        }
    }

    private static IEnumerable<AiStreamToken> ExtractTokens(
        JsonElement root,
        Dictionary<int, (string? Id, string? Name, StringBuilder Args)>? pendingToolCalls)
    {
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];

            if (firstChoice.TryGetProperty("delta", out var delta))
            {
                foreach (var token in ExtractMessageTokens(delta, pendingToolCalls))
                {
                    yield return token;
                }
            }
            else if (firstChoice.TryGetProperty("message", out var message))
            {
                foreach (var token in ExtractMessageTokens(message, pendingToolCalls))
                {
                    yield return token;
                }
            }
        }

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

    private static IEnumerable<AiStreamToken> ExtractMessageTokens(
        JsonElement message,
        Dictionary<int, (string? Id, string? Name, StringBuilder Args)>? pendingToolCalls)
    {
        if (message.TryGetProperty("reasoning_content", out var reasoning))
        {
            var reasoningText = reasoning.GetString();
            if (!string.IsNullOrEmpty(reasoningText))
            {
                yield return new AiStreamToken(AiStreamTokenType.Reasoning, reasoningText);
            }
        }

        if (message.TryGetProperty("content", out var content))
        {
            var text = ExtractContentText(content);

            if (!string.IsNullOrEmpty(text))
            {
                yield return new AiStreamToken(AiStreamTokenType.Content, text);
            }
        }

        if (message.TryGetProperty("tool_calls", out var toolCalls) &&
            toolCalls.ValueKind == JsonValueKind.Array)
        {
            var callIndex = 0;
            foreach (var toolCall in toolCalls.EnumerateArray())
            {
                var index = toolCall.TryGetProperty("index", out var idx) ? idx.GetInt32() : callIndex++;
                var id = toolCall.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
                var functionName = string.Empty;
                var arguments = string.Empty;

                if (toolCall.TryGetProperty("function", out var function))
                {
                    functionName = function.TryGetProperty("name", out var name)
                        ? name.GetString() ?? string.Empty
                        : string.Empty;
                    arguments = function.TryGetProperty("arguments", out var args)
                        ? args.GetString() ?? string.Empty
                        : string.Empty;
                }

                if (pendingToolCalls is not null)
                {
                    if (!pendingToolCalls.TryGetValue(index, out var entry))
                    {
                        entry = (null, null, new StringBuilder());
                    }

                    if (!string.IsNullOrEmpty(id))
                    {
                        entry.Id = id;
                    }

                    if (!string.IsNullOrEmpty(functionName))
                    {
                        entry.Name = functionName;
                    }

                    if (!string.IsNullOrEmpty(arguments))
                    {
                        entry.Args.Append(arguments);
                    }

                    pendingToolCalls[index] = entry;

                    if (!string.IsNullOrWhiteSpace(entry.Name))
                    {
                        yield return new AiStreamToken(
                            AiStreamTokenType.ToolCall,
                            string.Empty,
                            ToolCall: new AiStreamToolCall(index, entry.Id ?? string.Empty, entry.Name, entry.Args.ToString()));
                    }

                    continue;
                }

                if (!string.IsNullOrWhiteSpace(functionName))
                {
                    yield return new AiStreamToken(
                        AiStreamTokenType.ToolCall,
                        string.Empty,
                        ToolCall: new AiStreamToolCall(index, id, functionName, arguments));
                }
            }
        }
    }

    private static string? ExtractContentText(JsonElement content)
    {
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => content.GetRawText()
        };
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
