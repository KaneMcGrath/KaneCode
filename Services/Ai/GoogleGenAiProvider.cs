using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Google.GenAI;
using Google.GenAI.Types;

namespace KaneCode.Services.Ai;

/// <summary>
/// AI provider that communicates with Google's Gemini models through the
/// <c>Google.GenAI</c> SDK. Supports both the Gemini Developer API (via API key)
/// and Gemini Enterprise Agent Platform API (via GCP service account).
///
/// The provider accepts an API key from <see cref="AiProviderSettings.ApiKey"/>
/// or falls back to the <c>GEMINI_API_KEY</c> environment variable.
/// </summary>
internal sealed class GoogleGenAiProvider : IAiProvider, IDisposable
{
    private readonly AiProviderSettings _settings;
    private IReadOnlyList<string> _availableModels;
    private Client? _client;
    private readonly object _clientLock = new();
    private bool _disposed;

    /// <summary>
    /// Well-known Gemini model identifiers used as fallback defaults
    /// when model discovery has not yet completed or fails.
    /// </summary>
    private static readonly IReadOnlyList<string> DefaultModels = new List<string>
    {
        "gemini-2.0-flash",
        "gemini-2.0-flash-lite",
        "gemini-2.5-flash",
        "gemini-2.5-pro",
        "gemini-1.5-flash",
        "gemini-1.5-pro",
    };

    public GoogleGenAiProvider(AiProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _availableModels = DefaultModels;
    }

    public string DisplayName => string.IsNullOrWhiteSpace(_settings.Label)
        ? "Google Gemini"
        : _settings.Label;

    public string ProviderId => "googlegemini";

    public bool SupportsImages => true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.ApiKey);

    public IReadOnlyList<string> AvailableModels => _availableModels;

    /// <summary>
    /// Returns the singleton <see cref="Client"/> instance, creating it if needed.
    /// The client is configured with the API key from settings or environment variable.
    /// </summary>
    private Client GetOrCreateClient()
    {
        if (_client is not null)
        {
            return _client;
        }

        lock (_clientLock)
        {
            if (_client is not null)
            {
                return _client;
            }

            if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            {
                _client = new Client(apiKey: _settings.ApiKey);
            }
            else
            {
                // Fall back to environment variable (GEMINI_API_KEY)
                _client = new Client();
            }

            return _client;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return _availableModels;
        }

        try
        {
            Client clientInstance = GetOrCreateClient();

            // ListAsync returns a pager that can be enumerated asynchronously
            List<string> discoveredModels = [];
            HashSet<string> seenModels = new(StringComparer.OrdinalIgnoreCase);

            object pager = await clientInstance.Models.ListAsync(
                config: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await foreach (Model model in (IAsyncEnumerable<Model>)pager)
            {
                string? modelName = model.Name;
                if (string.IsNullOrWhiteSpace(modelName))
                {
                    continue;
                }

                // The model name is typically "models/gemini-2.0-flash" — extract
                // just the short ID for display consistency.
                string modelId = modelName;
                const string ModelsPrefix = "models/";
                if (modelId.StartsWith(ModelsPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    modelId = modelId[ModelsPrefix.Length..];
                }

                if (seenModels.Add(modelId))
                {
                    discoveredModels.Add(modelId);
                }
            }

            if (discoveredModels.Count > 0)
            {
                _availableModels = MergeAvailableModels(discoveredModels, _settings.SelectedModel);
            }
        }
        catch (HttpRequestException ex)
        {
            Debug.WriteLine($"Google GenAI model discovery request failed: {ex.Message}");
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"Google GenAI model discovery returned unexpected payload: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Debug.WriteLine("Google GenAI model discovery timed out.");
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"Google GenAI model discovery failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"Google GenAI model discovery failed: {ex.Message}");
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

        string resolvedModel = ResolveModel(model, _settings.SelectedModel);
        Client clientInstance = GetOrCreateClient();

        // Separate system messages from regular conversation messages
        List<AiChatMessage> systemMessages = [];
        List<AiChatMessage> conversationMessages = [];
        foreach (AiChatMessage msg in messages)
        {
            if (msg.Role == AiChatRole.System)
            {
                systemMessages.Add(msg);
            }
            else
            {
                conversationMessages.Add(msg);
            }
        }

        // Build the Google GenAI contents from conversation messages
        List<Content> googleContents = BuildContents(conversationMessages);

        // Build the configuration with system instruction, inference params, and tools
        GenerateContentConfig config = BuildConfig(systemMessages, tools);

        if (streamResponse)
        {
            // Streaming path
            await foreach (GenerateContentResponse chunk in clientInstance.Models
                .GenerateContentStreamAsync(resolvedModel, googleContents, config, cancellationToken)
                .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (AiStreamToken token in ExtractTokensFromChunk(chunk))
                {
                    yield return token;
                }
            }
        }
        else
        {
            // Non-streaming path
            GenerateContentResponse response = await clientInstance.Models
                .GenerateContentAsync(resolvedModel, googleContents, config, cancellationToken)
                .ConfigureAwait(false);

            foreach (AiStreamToken token in ExtractTokensFromChunk(response))
            {
                yield return token;
            }
        }
    }

    /// <summary>
    /// Builds a <see cref="GenerateContentConfig"/> from system messages, inference parameters,
    /// and tool definitions (converted from OpenAI format).
    /// </summary>
    private GenerateContentConfig BuildConfig(
        IReadOnlyList<AiChatMessage> systemMessages,
        JsonElement tools)
    {
        GenerateContentConfig config = new()
        {
            // Map inference parameters from settings to the Google GenAI config
            Temperature = _settings.Temperature,
            TopP = _settings.TopP,
        };

        // Map TopK — note that TopK on AiProviderSettings is int? which is compatible
        if (_settings.TopK.HasValue)
        {
            config.TopK = _settings.TopK.Value;
        }

        // System instruction — concatenate all system messages into one Content
        if (systemMessages.Count > 0)
        {
            StringBuilder systemTextBuilder = new();
            foreach (AiChatMessage sysMsg in systemMessages)
            {
                if (!string.IsNullOrWhiteSpace(sysMsg.Content))
                {
                    if (systemTextBuilder.Length > 0)
                    {
                        systemTextBuilder.Append('\n');
                    }

                    systemTextBuilder.Append(sysMsg.Content);
                }
            }

            if (systemTextBuilder.Length > 0)
            {
                config.SystemInstruction = new Content
                {
                    Parts = [new Part { Text = systemTextBuilder.ToString() }]
                };
            }
        }

        // Tools — convert from OpenAI format to Google GenAI format
        List<Tool> googleTools = ConvertToolsFromOpenAiFormat(tools);
        if (googleTools.Count > 0)
        {
            config.Tools = googleTools;
        }

        return config;
    }

    /// <summary>
    /// Converts an OpenAI-format tools JSON array to Google GenAI <see cref="Tool"/> objects.
    /// </summary>
    private static List<Tool> ConvertToolsFromOpenAiFormat(JsonElement tools)
    {
        List<Tool> result = [];

        if (tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() == 0)
        {
            return result;
        }

        List<FunctionDeclaration> functionDeclarations = [];

        foreach (JsonElement toolElement in tools.EnumerateArray())
        {
            if (!toolElement.TryGetProperty("type", out JsonElement typeElement))
            {
                continue;
            }

            string type = typeElement.GetString() ?? string.Empty;
            if (!string.Equals(type, "function", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!toolElement.TryGetProperty("function", out JsonElement functionElement))
            {
                continue;
            }

            string? name = functionElement.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString()
                : null;

            string? description = functionElement.TryGetProperty("description", out JsonElement descElement)
                ? descElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            Schema? parameters = null;
            if (functionElement.TryGetProperty("parameters", out JsonElement paramsElement))
            {
                parameters = ConvertSchemaFromOpenAiFormat(paramsElement);
            }

            FunctionDeclaration declaration = new()
            {
                Name = name,
                Description = description ?? string.Empty,
                Parameters = parameters
            };

            functionDeclarations.Add(declaration);
        }

        if (functionDeclarations.Count > 0)
        {
            result.Add(new Tool { FunctionDeclarations = functionDeclarations });
        }

        return result;
    }

    /// <summary>
    /// Converts an OpenAI-format JSON Schema element to a Google GenAI <see cref="Schema"/>.
    /// </summary>
    private static Schema? ConvertSchemaFromOpenAiFormat(JsonElement schemaElement)
    {
        if (schemaElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        Schema schema = new();

        // Type
        if (schemaElement.TryGetProperty("type", out JsonElement typeElement))
        {
            string typeStr = typeElement.GetString() ?? "string";
            schema.Type = MapOpenAiTypeToGoogleType(typeStr);
        }

        // Title
        if (schemaElement.TryGetProperty("title", out JsonElement titleElement))
        {
            schema.Title = titleElement.GetString() ?? string.Empty;
        }

        // Description
        if (schemaElement.TryGetProperty("description", out JsonElement descElement))
        {
            schema.Description = descElement.GetString() ?? string.Empty;
        }

        // Properties (recursive)
        if (schemaElement.TryGetProperty("properties", out JsonElement propertiesElement) &&
            propertiesElement.ValueKind == JsonValueKind.Object)
        {
            schema.Properties = new Dictionary<string, Schema>();

            foreach (JsonProperty property in propertiesElement.EnumerateObject())
            {
                Schema? propertySchema = ConvertSchemaFromOpenAiFormat(property.Value);
                if (propertySchema is not null)
                {
                    schema.Properties[property.Name] = propertySchema;
                }
            }
        }

        // Required
        if (schemaElement.TryGetProperty("required", out JsonElement requiredElement) &&
            requiredElement.ValueKind == JsonValueKind.Array)
        {
            List<string> required = [];
            foreach (JsonElement item in requiredElement.EnumerateArray())
            {
                string? itemStr = item.GetString();
                if (!string.IsNullOrWhiteSpace(itemStr))
                {
                    required.Add(itemStr);
                }
            }

            schema.Required = required;
        }

        // Enum values
        if (schemaElement.TryGetProperty("enum", out JsonElement enumElement) &&
            enumElement.ValueKind == JsonValueKind.Array)
        {
            List<string> enumValues = [];
            foreach (JsonElement item in enumElement.EnumerateArray())
            {
                string? itemStr = item.GetString();
                if (itemStr is not null)
                {
                    enumValues.Add(itemStr);
                }
            }

            schema.Enum = enumValues;
        }

        // Items (for array types)
        if (schemaElement.TryGetProperty("items", out JsonElement itemsElement) &&
            itemsElement.ValueKind == JsonValueKind.Object)
        {
            schema.Items = ConvertSchemaFromOpenAiFormat(itemsElement);
        }

        return schema;
    }

    /// <summary>
    /// Maps an OpenAI-style JSON Schema type string to a Google GenAI <see cref="Google.GenAI.Types.Type"/> enum.
    /// </summary>
    private static Google.GenAI.Types.Type MapOpenAiTypeToGoogleType(string type)
    {
        // The Google GenAI SDK enum values follow PascalCase naming conventions
        return type.ToLowerInvariant() switch
        {
            "string" => Google.GenAI.Types.Type.String,
            "integer" or "number" => Google.GenAI.Types.Type.Integer,
            "boolean" => Google.GenAI.Types.Type.Boolean,
            "array" => Google.GenAI.Types.Type.Array,
            "object" => Google.GenAI.Types.Type.Object,
            _ => Google.GenAI.Types.Type.String
        };
    }

    /// <summary>
    /// Builds a list of Google GenAI <see cref="Content"/> objects from the conversation messages.
    /// </summary>
    private static List<Content> BuildContents(IReadOnlyList<AiChatMessage> messages)
    {
        List<Content> contents = [];

        foreach (AiChatMessage msg in messages)
        {
            Content content = new()
            {
                Role = MapRole(msg.Role),
                Parts = BuildParts(msg)
            };

            contents.Add(content);
        }

        return contents;
    }

    /// <summary>
    /// Maps an <see cref="AiChatRole"/> to the Google GenAI role string.
    /// </summary>
    private static string MapRole(AiChatRole role)
    {
        return role switch
        {
            AiChatRole.User => "user",
            AiChatRole.Assistant => "model",
            AiChatRole.Tool => "function",
            _ => "user"
        };
    }

    /// <summary>
    /// Builds the list of <see cref="Part"/> objects for a message, handling
    /// text content, images, tool calls, and tool responses.
    /// </summary>
    private static List<Part> BuildParts(AiChatMessage msg)
    {
        List<Part> parts = [];

        // Tool role messages contain function response data
        if (msg.Role == AiChatRole.Tool)
        {
            if (!string.IsNullOrWhiteSpace(msg.ToolCallId))
            {
                // Parse the content as a JSON response object
                Dictionary<string, object?>? responseDict = null;
                try
                {
                    responseDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(msg.Content);
                }
                catch (JsonException)
                {
                    responseDict = [];
                }

                parts.Add(new Part
                {
                    FunctionResponse = new FunctionResponse
                    {
                        Name = msg.ToolCallId,
                        Response = ConvertToDictionaryObject(responseDict ?? [])
                    }
                });
            }

            return parts;
        }

        // Assistant messages with tool calls
        if (msg.Role == AiChatRole.Assistant && msg.ToolCalls is { Count: > 0 })
        {
            foreach (AiToolCallRequest toolCall in msg.ToolCalls)
            {
                Dictionary<string, object?>? argsDict = null;
                try
                {
                    argsDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(toolCall.ArgumentsJson);
                }
                catch (JsonException)
                {
                    // If arguments can't be parsed, pass an empty dictionary
                    argsDict = [];
                }

                parts.Add(new Part
                {
                    FunctionCall = new FunctionCall
                    {
                        Name = toolCall.FunctionName,
                        Args = ConvertToDictionaryObject(argsDict ?? [])
                    }
                });
            }

            return parts;
        }

        // Text content (may also include images for user/tool messages)
        bool hasImages = msg.Images is { Count: > 0 };

        if (!hasImages)
        {
            // Simple text-only message
            if (!string.IsNullOrWhiteSpace(msg.Content))
            {
                parts.Add(new Part { Text = msg.Content });
            }
        }
        else
        {
            // Multi-modal message with text + images
            if (!string.IsNullOrWhiteSpace(msg.Content))
            {
                parts.Add(new Part { Text = msg.Content });
            }

            foreach (AiChatImagePart image in msg.Images)
            {
                // Convert the base64 string data to a byte array for the SDK
                byte[] imageData;
                try
                {
                    imageData = Convert.FromBase64String(image.Base64Data);
                }
                catch (FormatException)
                {
                    // If the data is not valid base64, skip this image
                    continue;
                }

                parts.Add(new Part
                {
                    InlineData = new Blob
                    {
                        MimeType = image.MimeType,
                        Data = imageData
                    }
                });
            }
        }

        return parts;
    }

    /// <summary>
    /// Converts a <c>Dictionary&lt;string, object?&gt;</c> to the <c>Dictionary&lt;string, object&gt;</c>
    /// format expected by <see cref="FunctionCall.Args"/> and <see cref="FunctionResponse.Response"/>.
    /// </summary>
    private static Dictionary<string, object> ConvertToDictionaryObject(Dictionary<string, object?> source)
    {
        Dictionary<string, object> result = [];
        foreach (KeyValuePair<string, object?> kvp in source)
        {
            result[kvp.Key] = kvp.Value ?? string.Empty;
        }

        return result;
    }

    /// <summary>
    /// Extracts stream tokens from a single Google GenAI streaming response chunk
    /// or a buffered completion response.
    /// </summary>
    private static IEnumerable<AiStreamToken> ExtractTokensFromChunk(GenerateContentResponse chunk)
    {
        // Usage metadata — handle nullable ints safely
        if (chunk.UsageMetadata is not null)
        {
            int promptTokens = chunk.UsageMetadata.PromptTokenCount ?? 0;
            int completionTokens = chunk.UsageMetadata.CandidatesTokenCount ?? 0;
            int totalTokens = chunk.UsageMetadata.TotalTokenCount ?? 0;

            yield return new AiStreamToken(
                AiStreamTokenType.Usage,
                string.Empty,
                new AiUsageStats(promptTokens, completionTokens, totalTokens));
        }

        // Candidates
        if (chunk.Candidates is { Count: > 0 })
        {
            Candidate candidate = chunk.Candidates[0];

            // Content parts
            if (candidate.Content?.Parts is { Count: > 0 })
            {
                foreach (Part part in candidate.Content.Parts)
                {
                    // Regular text content
                    if (!string.IsNullOrWhiteSpace(part.Text))
                    {
                        yield return new AiStreamToken(AiStreamTokenType.Content, part.Text);
                    }

                    // Function call / tool call
                    if (part.FunctionCall is not null)
                    {
                        string argsJson = part.FunctionCall.Args is { Count: > 0 }
                            ? JsonSerializer.Serialize(part.FunctionCall.Args)
                            : "{}";

                        yield return new AiStreamToken(
                            AiStreamTokenType.ToolCall,
                            string.Empty,
                            ToolCall: new AiStreamToolCall(
                                Index: 0,
                                Id: part.FunctionCall.Name ?? string.Empty,
                                FunctionName: part.FunctionCall.Name ?? string.Empty,
                                ArgumentsJson: argsJson));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Resolves the effective model name, preferring the explicitly provided
    /// <paramref name="requestedModel"/> over the configured default.
    /// </summary>
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

        return "gemini-2.0-flash";
    }

    /// <summary>
    /// Merges a list of discovered models with the user's selected model,
    /// promoting it to the top of the list when found.
    /// </summary>
    private static IReadOnlyList<string> MergeAvailableModels(
        IReadOnlyList<string> discoveredModels,
        string? selectedModel)
    {
        ArgumentNullException.ThrowIfNull(discoveredModels);

        List<string> mergedModels = [];
        HashSet<string> seenModels = new(StringComparer.OrdinalIgnoreCase);

        // Promote the selected model to the top if it's among the discovered models
        bool selectedModelFound = false;
        if (!string.IsNullOrWhiteSpace(selectedModel))
        {
            foreach (string discoveredModel in discoveredModels)
            {
                if (!string.IsNullOrWhiteSpace(discoveredModel) &&
                    string.Equals(discoveredModel, selectedModel, StringComparison.OrdinalIgnoreCase))
                {
                    selectedModelFound = true;
                    break;
                }
            }
        }

        if (selectedModelFound)
        {
            mergedModels.Add(selectedModel!);
            seenModels.Add(selectedModel!);
        }

        foreach (string discoveredModel in discoveredModels)
        {
            if (string.IsNullOrWhiteSpace(discoveredModel) || !seenModels.Add(discoveredModel))
            {
                continue;
            }

            mergedModels.Add(discoveredModel);
        }

        return mergedModels;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // The Google.GenAI Client does not implement IDisposable,
        // so nothing to dispose here beyond the lock object.
    }
}
