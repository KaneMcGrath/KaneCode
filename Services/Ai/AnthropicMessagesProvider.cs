using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace KaneCode.Services.Ai;

/// <summary>
/// AI provider for the Anthropic-compatible <c>/v1/messages</c> API implemented by
/// Anthropic, llama.cpp, and other servers. Translates KaneCode's provider-neutral
/// messages and OpenAI-format tool definitions into Messages API content blocks, and
/// supports both buffered JSON responses and server-sent event streams.
/// </summary>
internal sealed class AnthropicMessagesProvider : IAiProvider, IDisposable
{
    internal const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    internal const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly AiProviderSettings _settings;
    private IReadOnlyList<string> _availableModels;
    private bool _disposed;

    public AnthropicMessagesProvider(AiProviderSettings settings)
        : this(settings, new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, ownsHttpClient: true)
    {
    }

    internal AnthropicMessagesProvider(
        AiProviderSettings settings,
        HttpClient httpClient,
        bool ownsHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(httpClient);

        _settings = settings;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _availableModels = GetFallbackModels();
    }

    public string DisplayName => string.IsNullOrWhiteSpace(_settings.Label)
        ? "/v1/messages"
        : _settings.Label;

    public string ProviderId => "anthropicmessages";

    public bool SupportsImages => true;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settings.Endpoint) ||
        !string.IsNullOrWhiteSpace(_settings.ApiKey);

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
            AddMessagesApiHeaders(request, _settings.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

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
            Debug.WriteLine("Messages API model discovery request failed.");
        }
        catch (InvalidOperationException)
        {
            Debug.WriteLine("Messages API model discovery could not build a valid models endpoint.");
        }
        catch (JsonException)
        {
            Debug.WriteLine("Messages API model discovery returned an unexpected JSON payload.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Debug.WriteLine("Messages API model discovery timed out.");
        }
        catch (NotSupportedException)
        {
            Debug.WriteLine("Messages API model discovery endpoint scheme is not supported.");
        }
        catch (UriFormatException)
        {
            Debug.WriteLine("Messages API model discovery endpoint is malformed.");
        }

        return _availableModels;
    }

    internal string BuildRawRequestJson(
        IReadOnlyList<AiChatMessage> messages,
        string model,
        JsonElement tools,
        bool streamResponse)
    {
        ArgumentNullException.ThrowIfNull(messages);

        string resolvedModel = ResolveModel(model, _settings.SelectedModel);
        return BuildMessagesRequestJson(messages, resolvedModel, tools, _settings, streamResponse);
    }

    internal string GetMessagesEndpoint()
    {
        return BuildMessagesUrl(_settings.Endpoint);
    }

    public async IAsyncEnumerable<AiStreamToken> StreamCompletionAsync(
        IReadOnlyList<AiChatMessage> messages,
        string model,
        JsonElement tools = default,
        bool streamResponse = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        string resolvedModel = ResolveModel(model, _settings.SelectedModel);
        string url = BuildMessagesUrl(_settings.Endpoint);
        string json = BuildMessagesRequestJson(messages, resolvedModel, tools, _settings, streamResponse);

        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        AddMessagesApiHeaders(request, _settings.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            streamResponse ? "text/event-stream" : "application/json"));

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            streamResponse ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Messages API request failed with {(int)response.StatusCode} ({response.ReasonPhrase}). {errorBody}".Trim());
        }

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            foreach (AiStreamToken token in ExtractCompletionTokens(responseJson))
            {
                yield return token;
            }

            yield break;
        }

        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using StreamReader reader = new(stream, Encoding.UTF8);
        StringBuilder eventData = new();
        AnthropicStreamState state = new();

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(line))
            {
                if (eventData.Length > 0)
                {
                    foreach (AiStreamToken token in ExtractStreamTokens(eventData.ToString(), state))
                    {
                        yield return token;
                    }

                    eventData.Clear();
                }

                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            if (eventData.Length > 0)
            {
                eventData.Append('\n');
            }

            eventData.Append(line.AsSpan("data:".Length).TrimStart());
        }

        if (eventData.Length > 0)
        {
            foreach (AiStreamToken token in ExtractStreamTokens(eventData.ToString(), state))
            {
                yield return token;
            }
        }

        if (!state.UsageEmitted && state.HasUsage)
        {
            yield return state.CreateUsageToken();
        }
    }

    internal static string BuildMessagesRequestJson(
        IReadOnlyList<AiChatMessage> messages,
        string model,
        JsonElement tools,
        AiProviderSettings settings,
        bool streamResponse)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(settings);

        using MemoryStream bodyStream = new();
        using (Utf8JsonWriter writer = new(bodyStream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteNumber(
                "max_tokens",
                settings.MaxOutputTokens is > 0
                    ? settings.MaxOutputTokens.Value
                    : AiProviderSettings.DefaultMaxOutputTokens);
            writer.WriteBoolean("stream", streamResponse);

            string systemPrompt = BuildSystemPrompt(messages);
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                writer.WriteString("system", systemPrompt);
            }

            writer.WritePropertyName("messages");
            WriteMessages(writer, messages);

            if (HasValidTools(tools))
            {
                writer.WritePropertyName("tools");
                WriteTools(writer, tools);
            }

            if (settings.Temperature.HasValue)
            {
                writer.WriteNumber("temperature", settings.Temperature.Value);
            }

            if (settings.TopP.HasValue)
            {
                writer.WriteNumber("top_p", settings.TopP.Value);
            }

            if (settings.TopK.HasValue)
            {
                writer.WriteNumber("top_k", settings.TopK.Value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(bodyStream.ToArray());
    }

    private static string BuildSystemPrompt(IReadOnlyList<AiChatMessage> messages)
    {
        StringBuilder builder = new();
        foreach (AiChatMessage message in messages)
        {
            if (message.Role != AiChatRole.System || string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append(message.Content);
        }

        return builder.ToString();
    }

    private static void WriteMessages(Utf8JsonWriter writer, IReadOnlyList<AiChatMessage> messages)
    {
        List<AiChatMessage> conversationMessages = [];
        foreach (AiChatMessage message in messages)
        {
            if (message.Role != AiChatRole.System && HasContentBlocks(message))
            {
                conversationMessages.Add(message);
            }
        }

        writer.WriteStartArray();
        int messageIndex = 0;
        while (messageIndex < conversationMessages.Count)
        {
            string role = MapRole(conversationMessages[messageIndex].Role);
            writer.WriteStartObject();
            writer.WriteString("role", role);
            writer.WriteStartArray("content");

            while (messageIndex < conversationMessages.Count &&
                   string.Equals(MapRole(conversationMessages[messageIndex].Role), role, StringComparison.Ordinal))
            {
                WriteContentBlocks(writer, conversationMessages[messageIndex]);
                messageIndex++;
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static bool HasContentBlocks(AiChatMessage message)
    {
        return message.Role switch
        {
            AiChatRole.Tool => !string.IsNullOrWhiteSpace(message.ToolCallId),
            AiChatRole.Assistant => !string.IsNullOrWhiteSpace(message.Content) || message.ToolCalls is { Count: > 0 },
            AiChatRole.User => !string.IsNullOrWhiteSpace(message.Content) || message.Images is { Count: > 0 },
            _ => false
        };
    }

    private static string MapRole(AiChatRole role)
    {
        return role == AiChatRole.Assistant ? "assistant" : "user";
    }

    private static void WriteContentBlocks(Utf8JsonWriter writer, AiChatMessage message)
    {
        if (message.Role == AiChatRole.Tool)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "tool_result");
            writer.WriteString("tool_use_id", message.ToolCallId);
            writer.WriteString("content", message.Content);
            writer.WriteEndObject();
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", message.Content);
            writer.WriteEndObject();
        }

        if (message.Role == AiChatRole.User && message.Images is { Count: > 0 })
        {
            foreach (AiChatImagePart image in message.Images)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "image");
                writer.WriteStartObject("source");
                writer.WriteString("type", "base64");
                writer.WriteString("media_type", image.MimeType);
                writer.WriteString("data", image.Base64Data);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
        }

        if (message.Role == AiChatRole.Assistant && message.ToolCalls is { Count: > 0 })
        {
            foreach (AiToolCallRequest toolCall in message.ToolCalls)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "tool_use");
                writer.WriteString("id", toolCall.Id);
                writer.WriteString("name", toolCall.FunctionName);
                writer.WritePropertyName("input");
                WriteJsonObjectOrEmpty(writer, toolCall.ArgumentsJson);
                writer.WriteEndObject();
            }
        }
    }

    private static bool HasValidTools(JsonElement tools)
    {
        if (tools.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement tool in tools.EnumerateArray())
        {
            if (TryGetToolFunction(tool, out JsonElement _, out string _))
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteTools(Utf8JsonWriter writer, JsonElement tools)
    {
        writer.WriteStartArray();
        foreach (JsonElement tool in tools.EnumerateArray())
        {
            if (!TryGetToolFunction(tool, out JsonElement function, out string name))
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("name", name);

            if (function.TryGetProperty("description", out JsonElement descriptionElement) &&
                descriptionElement.ValueKind == JsonValueKind.String)
            {
                writer.WriteString("description", descriptionElement.GetString());
            }

            writer.WritePropertyName("input_schema");
            if (function.TryGetProperty("parameters", out JsonElement parametersElement) &&
                parametersElement.ValueKind == JsonValueKind.Object)
            {
                parametersElement.WriteTo(writer);
            }
            else
            {
                WriteEmptyObjectSchema(writer);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static bool TryGetToolFunction(JsonElement tool, out JsonElement function, out string name)
    {
        function = default;
        name = string.Empty;

        if (tool.ValueKind != JsonValueKind.Object ||
            !tool.TryGetProperty("type", out JsonElement typeElement) ||
            !string.Equals(typeElement.GetString(), "function", StringComparison.OrdinalIgnoreCase) ||
            !tool.TryGetProperty("function", out function) ||
            function.ValueKind != JsonValueKind.Object ||
            !function.TryGetProperty("name", out JsonElement nameElement))
        {
            return false;
        }

        name = nameElement.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(name);
    }

    private static void WriteJsonObjectOrEmpty(Utf8JsonWriter writer, string? json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    document.RootElement.WriteTo(writer);
                    return;
                }
            }
            catch (JsonException)
            {
                // A malformed tool call should still result in a valid Anthropic request.
            }
        }

        writer.WriteStartObject();
        writer.WriteEndObject();
    }

    private static void WriteEmptyObjectSchema(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    internal static IReadOnlyList<AiStreamToken> ExtractCompletionTokens(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        ThrowIfErrorResponse(root);

        List<AiStreamToken> tokens = [];
        if (root.TryGetProperty("content", out JsonElement contentElement) &&
            contentElement.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement block in contentElement.EnumerateArray())
            {
                string type = GetString(block, "type");
                if (type == "text")
                {
                    string text = GetString(block, "text");
                    if (text.Length > 0)
                    {
                        tokens.Add(new AiStreamToken(AiStreamTokenType.Content, text));
                    }
                }
                else if (type == "thinking")
                {
                    string thinking = GetString(block, "thinking");
                    if (thinking.Length > 0)
                    {
                        tokens.Add(new AiStreamToken(AiStreamTokenType.Reasoning, thinking));
                    }
                }
                else if (type == "tool_use")
                {
                    string id = GetString(block, "id");
                    string name = GetString(block, "name");
                    string arguments = block.TryGetProperty("input", out JsonElement inputElement)
                        ? inputElement.GetRawText()
                        : "{}";
                    tokens.Add(new AiStreamToken(
                        AiStreamTokenType.ToolCall,
                        string.Empty,
                        ToolCall: new AiStreamToolCall(index, id, name, arguments)));
                }

                index++;
            }
        }

        if (root.TryGetProperty("usage", out JsonElement usageElement))
        {
            int promptTokens = GetPromptTokenCount(usageElement);
            int completionTokens = GetInt32(usageElement, "output_tokens");
            tokens.Add(new AiStreamToken(
                AiStreamTokenType.Usage,
                string.Empty,
                new AiUsageStats(promptTokens, completionTokens, promptTokens + completionTokens)));
        }

        return tokens;
    }

    internal static IReadOnlyList<AiStreamToken> ExtractStreamTokens(
        string json,
        AnthropicStreamState state)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(state);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        ThrowIfErrorResponse(root);

        List<AiStreamToken> tokens = [];
        string eventType = GetString(root, "type");

        if (eventType == "message_start" &&
            root.TryGetProperty("message", out JsonElement messageElement) &&
            messageElement.TryGetProperty("usage", out JsonElement startUsage))
        {
            state.PromptTokens = GetPromptTokenCount(startUsage);
            state.CompletionTokens = GetInt32(startUsage, "output_tokens");
            state.HasUsage = true;
        }
        else if (eventType == "content_block_start")
        {
            int index = GetInt32(root, "index");
            if (root.TryGetProperty("content_block", out JsonElement block) &&
                GetString(block, "type") == "tool_use")
            {
                PendingAnthropicToolCall pending = new(
                    GetString(block, "id"),
                    GetString(block, "name"));

                if (block.TryGetProperty("input", out JsonElement input) &&
                    input.ValueKind == JsonValueKind.Object &&
                    input.EnumerateObject().Any())
                {
                    pending.Arguments.Append(input.GetRawText());
                }

                state.PendingToolCalls[index] = pending;
            }
        }
        else if (eventType == "content_block_delta" &&
                 root.TryGetProperty("delta", out JsonElement delta))
        {
            int index = GetInt32(root, "index");
            string deltaType = GetString(delta, "type");
            if (deltaType == "text_delta")
            {
                string text = GetString(delta, "text");
                if (text.Length > 0)
                {
                    tokens.Add(new AiStreamToken(AiStreamTokenType.Content, text));
                }
            }
            else if (deltaType == "thinking_delta")
            {
                string thinking = GetString(delta, "thinking");
                if (thinking.Length > 0)
                {
                    tokens.Add(new AiStreamToken(AiStreamTokenType.Reasoning, thinking));
                }
            }
            else if (deltaType == "input_json_delta" &&
                     state.PendingToolCalls.TryGetValue(index, out PendingAnthropicToolCall? pending))
            {
                pending.Arguments.Append(GetString(delta, "partial_json"));
            }
        }
        else if (eventType == "content_block_stop")
        {
            int index = GetInt32(root, "index");
            if (state.PendingToolCalls.Remove(index, out PendingAnthropicToolCall? pending))
            {
                string arguments = pending.Arguments.Length > 0 ? pending.Arguments.ToString() : "{}";
                tokens.Add(new AiStreamToken(
                    AiStreamTokenType.ToolCall,
                    string.Empty,
                    ToolCall: new AiStreamToolCall(index, pending.Id, pending.Name, arguments)));
            }
        }
        else if (eventType == "message_delta" &&
                 root.TryGetProperty("usage", out JsonElement deltaUsage))
        {
            state.CompletionTokens = GetInt32(deltaUsage, "output_tokens");
            state.HasUsage = true;
        }
        else if (eventType == "message_stop" && state.HasUsage && !state.UsageEmitted)
        {
            state.UsageEmitted = true;
            tokens.Add(state.CreateUsageToken());
        }

        return tokens;
    }

    private static void ThrowIfErrorResponse(JsonElement root)
    {
        if (GetString(root, "type") != "error")
        {
            return;
        }

        string message = "The Messages API returned an error event.";
        if (root.TryGetProperty("error", out JsonElement errorElement))
        {
            string apiMessage = GetString(errorElement, "message");
            if (!string.IsNullOrWhiteSpace(apiMessage))
            {
                message = apiMessage;
            }
        }

        throw new InvalidOperationException(message);
    }

    private static int GetPromptTokenCount(JsonElement usage)
    {
        return GetInt32(usage, "input_tokens") +
               GetInt32(usage, "cache_creation_input_tokens") +
               GetInt32(usage, "cache_read_input_tokens");
    }

    private static int GetInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : 0;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
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
                string modelId = GetString(modelElement, "id");
                if (!string.IsNullOrWhiteSpace(modelId) && seenModels.Add(modelId))
                {
                    models.Add(modelId);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(selectedModel))
        {
            int selectedIndex = models.FindIndex(
                discovered => string.Equals(discovered, selectedModel, StringComparison.OrdinalIgnoreCase));
            if (selectedIndex > 0)
            {
                string selected = models[selectedIndex];
                models.RemoveAt(selectedIndex);
                models.Insert(0, selected);
            }
        }

        return models;
    }

    internal static string BuildMessagesUrl(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return DefaultEndpoint;
        }

        Uri uri = CreateAbsoluteUri(endpoint);
        string path = uri.AbsolutePath.TrimEnd('/');
        string targetPath = path switch
        {
            "" => "/v1/messages",
            _ when path.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase) => path,
            _ when path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) => path,
            _ when path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) => $"{path}/messages",
            _ => $"{path}/v1/messages"
        };

        UriBuilder builder = new(uri) { Path = targetPath, Query = string.Empty, Fragment = string.Empty };
        return builder.Uri.ToString();
    }

    internal static string BuildModelsUrl(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return "https://api.anthropic.com/v1/models";
        }

        Uri uri = CreateAbsoluteUri(endpoint);
        string path = uri.AbsolutePath.TrimEnd('/');
        string targetPath = path switch
        {
            "" => "/v1/models",
            _ when path.EndsWith("/v1/models", StringComparison.OrdinalIgnoreCase) => path,
            _ when path.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase) =>
                string.Concat(path.AsSpan(0, path.Length - "/messages".Length), "/models"),
            _ when path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) =>
                string.Concat(path.AsSpan(0, path.Length - "/messages".Length), "/models"),
            _ when path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) => $"{path}/models",
            _ => $"{path}/v1/models"
        };

        UriBuilder builder = new(uri) { Path = targetPath, Query = string.Empty, Fragment = string.Empty };
        return builder.Uri.ToString();
    }

    private static Uri CreateAbsoluteUri(string endpoint)
    {
        string normalized = endpoint.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = "http://" + normalized;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidOperationException("The configured Messages API endpoint must be an absolute URL.");
        }

        return uri;
    }

    private static void AddMessagesApiHeaders(HttpRequestMessage request, string apiKey)
    {
        request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);

        // Anthropic uses x-api-key. Many compatible/self-hosted servers, including
        // llama.cpp when API-key protection is enabled, use Bearer authentication.
        // Send Bearer auth only to non-Anthropic hosts so both conventions work.
        if (!IsAnthropicHostedEndpoint(request.RequestUri))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    private static bool IsAnthropicHostedEndpoint(Uri? endpoint)
    {
        if (endpoint is null)
        {
            return false;
        }

        return string.Equals(endpoint.Host, "api.anthropic.com", StringComparison.OrdinalIgnoreCase) ||
               endpoint.Host.EndsWith(".anthropic.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveModel(string requestedModel, string configuredModel)
    {
        if (!string.IsNullOrWhiteSpace(requestedModel))
        {
            return requestedModel;
        }

        return !string.IsNullOrWhiteSpace(configuredModel) ? configuredModel : "default";
    }

    private static IReadOnlyList<string> GetFallbackModels()
    {
        // Do not invent a vendor-specific model. Compatible servers expose their
        // loaded model through /v1/models, and the UI is populated after discovery.
        return [];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}

internal sealed class AnthropicStreamState
{
    public Dictionary<int, PendingAnthropicToolCall> PendingToolCalls { get; } = [];

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public bool HasUsage { get; set; }

    public bool UsageEmitted { get; set; }

    public AiStreamToken CreateUsageToken()
    {
        return new AiStreamToken(
            AiStreamTokenType.Usage,
            string.Empty,
            new AiUsageStats(PromptTokens, CompletionTokens, PromptTokens + CompletionTokens));
    }
}

internal sealed class PendingAnthropicToolCall
{
    public PendingAnthropicToolCall(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }

    public string Name { get; }

    public StringBuilder Arguments { get; } = new();
}
