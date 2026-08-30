using KaneCode.Services.Ai;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace KaneCode.Tests.Services.Ai;

public sealed class AnthropicMessagesProviderTests
{
    [Fact]
    public async Task WhenSendingRequestThenAnthropicHeadersAndMessagesEndpointAreUsed()
    {
        RecordingHttpMessageHandler handler = new();
        using HttpClient httpClient = new(handler);
        AiProviderSettings settings = new()
        {
            ApiKey = "secret-key",
            Endpoint = "https://example.test/v1",
            SelectedModel = "claude-test"
        };
        using AnthropicMessagesProvider provider = new(settings, httpClient);
        IReadOnlyList<AiChatMessage> messages = [new AiChatMessage(AiChatRole.User, "Hello")];
        List<AiStreamToken> tokens = [];

        await foreach (AiStreamToken token in provider.StreamCompletionAsync(
            messages,
            "claude-test",
            streamResponse: false))
        {
            tokens.Add(token);
        }

        Assert.Equal("https://example.test/v1/messages", handler.RequestUri?.ToString());
        Assert.Equal("secret-key", handler.ApiKey);
        Assert.Equal("Bearer secret-key", handler.Authorization);
        Assert.Equal(AnthropicMessagesProvider.AnthropicVersion, handler.AnthropicVersionHeader);
        Assert.Contains("\"model\":\"claude-test\"", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains(tokens, token => token.Type == AiStreamTokenType.Content && token.Text == "Hello back");
    }

    [Fact]
    public void WhenCreatedThenProviderMetadataIsMessagesApiSpecific()
    {
        AiProviderSettings settings = new()
        {
            ApiKey = "test-key"
        };

        using AnthropicMessagesProvider provider = new(settings);

        Assert.Equal("anthropicmessages", provider.ProviderId);
        Assert.Equal("/v1/messages", provider.DisplayName);
        Assert.True(provider.SupportsImages);
        Assert.True(provider.IsConfigured);
    }

    [Fact]
    public void WhenEndpointAndApiKeyAreMissingThenProviderIsNotConfigured()
    {
        AiProviderSettings settings = new();

        using AnthropicMessagesProvider provider = new(settings);

        Assert.False(provider.IsConfigured);
    }

    [Fact]
    public void WhenLocalEndpointHasNoApiKeyThenProviderIsConfiguredWithoutVendorModelFallback()
    {
        AiProviderSettings settings = new()
        {
            Endpoint = "http://localhost:8080"
        };

        using AnthropicMessagesProvider provider = new(settings);

        Assert.True(provider.IsConfigured);
        Assert.Empty(provider.AvailableModels);
    }

    [Fact]
    public async Task WhenDiscoveringModelsFromKeylessEndpointThenHostedModelIsReturned()
    {
        RecordingHttpMessageHandler handler = new(
            "{\"data\":[{\"id\":\"local-llama-model\"}]}");
        using HttpClient httpClient = new(handler);
        AiProviderSettings settings = new()
        {
            Endpoint = "http://localhost:8080/v1"
        };
        using AnthropicMessagesProvider provider = new(settings, httpClient);

        IReadOnlyList<string> models = await provider.GetAvailableModelsAsync();

        Assert.Equal(["local-llama-model"], models);
        Assert.Equal("http://localhost:8080/v1/models", handler.RequestUri?.ToString());
        Assert.Equal(string.Empty, handler.ApiKey);
        Assert.Equal(string.Empty, handler.Authorization);
    }

    [Fact]
    public void WhenBuildingRequestThenSystemMessagesAndInferenceSettingsUseAnthropicSchema()
    {
        IReadOnlyList<AiChatMessage> messages =
        [
            new AiChatMessage(AiChatRole.System, "First instruction"),
            new AiChatMessage(AiChatRole.System, "Second instruction"),
            new AiChatMessage(AiChatRole.User, "Hello")
        ];
        AiProviderSettings settings = new()
        {
            MaxOutputTokens = 4096,
            Temperature = 0.4,
            TopP = 0.8,
            TopK = 30,
            MinP = 0.1,
            PresencePenalty = 0.2,
            RepetitionPenalty = 1.1
        };

        string json = AnthropicMessagesProvider.BuildMessagesRequestJson(
            messages,
            "claude-test",
            default,
            settings,
            streamResponse: true);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal("claude-test", root.GetProperty("model").GetString());
        Assert.Equal(4096, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("First instruction\n\nSecond instruction", root.GetProperty("system").GetString());
        Assert.Equal(0.4, root.GetProperty("temperature").GetDouble());
        Assert.Equal(0.8, root.GetProperty("top_p").GetDouble());
        Assert.Equal(30, root.GetProperty("top_k").GetInt32());
        Assert.False(root.TryGetProperty("min_p", out JsonElement _));
        Assert.False(root.TryGetProperty("presence_penalty", out JsonElement _));
        Assert.False(root.TryGetProperty("repetition_penalty", out JsonElement _));
    }

    [Fact]
    public void WhenBuildingRequestThenImagesAndToolTurnsUseNativeContentBlocks()
    {
        IReadOnlyList<AiChatMessage> messages =
        [
            new AiChatMessage(AiChatRole.User, "Inspect this")
            {
                Images = [new AiChatImagePart("aGVsbG8=", "image/png")]
            },
            new AiChatMessage(AiChatRole.Assistant, "I will inspect it")
            {
                ToolCalls = [new AiToolCallRequest("toolu_1", "read_file", "{\"path\":\"a.cs\"}")]
            },
            new AiChatMessage(AiChatRole.Tool, "file one") { ToolCallId = "toolu_1" },
            new AiChatMessage(AiChatRole.Tool, "file two") { ToolCallId = "toolu_2" }
        ];
        AiProviderSettings settings = new();
        using JsonDocument toolsDocument = JsonDocument.Parse(
            """
            [{
              "type": "function",
              "function": {
                "name": "read_file",
                "description": "Reads a file",
                "parameters": {
                  "type": "object",
                  "properties": { "path": { "type": "string" } },
                  "required": ["path"]
                }
              }
            }]
            """);

        string json = AnthropicMessagesProvider.BuildMessagesRequestJson(
            messages,
            "claude-test",
            toolsDocument.RootElement,
            settings,
            streamResponse: false);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement serializedMessages = root.GetProperty("messages");

        Assert.Equal(3, serializedMessages.GetArrayLength());
        Assert.Equal("image", serializedMessages[0].GetProperty("content")[1].GetProperty("type").GetString());
        Assert.Equal("base64", serializedMessages[0].GetProperty("content")[1].GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("tool_use", serializedMessages[1].GetProperty("content")[1].GetProperty("type").GetString());
        Assert.Equal("a.cs", serializedMessages[1].GetProperty("content")[1].GetProperty("input").GetProperty("path").GetString());
        Assert.Equal("user", serializedMessages[2].GetProperty("role").GetString());
        Assert.Equal(2, serializedMessages[2].GetProperty("content").GetArrayLength());
        Assert.Equal("tool_result", serializedMessages[2].GetProperty("content")[0].GetProperty("type").GetString());

        JsonElement serializedTool = root.GetProperty("tools")[0];
        Assert.Equal("read_file", serializedTool.GetProperty("name").GetString());
        Assert.Equal("object", serializedTool.GetProperty("input_schema").GetProperty("type").GetString());
        Assert.False(serializedTool.TryGetProperty("type", out JsonElement _));
    }

    [Fact]
    public void WhenMaxOutputTokensIsInvalidThenRequestUsesDefault()
    {
        IReadOnlyList<AiChatMessage> messages = [new AiChatMessage(AiChatRole.User, "Hello")];
        AiProviderSettings settings = new() { MaxOutputTokens = 0 };

        string json = AnthropicMessagesProvider.BuildMessagesRequestJson(
            messages,
            "claude-test",
            default,
            settings,
            streamResponse: false);
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(
            AiProviderSettings.DefaultMaxOutputTokens,
            document.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void WhenParsingBufferedResponseThenContentToolsReasoningAndUsageAreReturned()
    {
        string json =
            """
            {
              "type": "message",
              "content": [
                { "type": "thinking", "thinking": "Considering" },
                { "type": "text", "text": "Done" },
                { "type": "tool_use", "id": "toolu_9", "name": "read_file", "input": { "path": "a.cs" } }
              ],
              "usage": {
                "input_tokens": 12,
                "cache_creation_input_tokens": 2,
                "cache_read_input_tokens": 3,
                "output_tokens": 4
              }
            }
            """;

        IReadOnlyList<AiStreamToken> tokens = AnthropicMessagesProvider.ExtractCompletionTokens(json);

        Assert.Equal(AiStreamTokenType.Reasoning, tokens[0].Type);
        Assert.Equal("Considering", tokens[0].Text);
        Assert.Equal(AiStreamTokenType.Content, tokens[1].Type);
        Assert.Equal("Done", tokens[1].Text);
        Assert.Equal("toolu_9", tokens[2].ToolCall?.Id);
        Assert.Equal("read_file", tokens[2].ToolCall?.FunctionName);
        Assert.Equal("a.cs", JsonDocument.Parse(tokens[2].ToolCall!.ArgumentsJson).RootElement.GetProperty("path").GetString());
        Assert.Equal(new AiUsageStats(17, 4, 21), tokens[3].UsageStats);
    }

    [Fact]
    public void WhenParsingStreamThenPartialToolJsonAndFinalUsageAreCombined()
    {
        AnthropicStreamState state = new();

        AnthropicMessagesProvider.ExtractStreamTokens(
            "{\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}",
            state);
        AnthropicMessagesProvider.ExtractStreamTokens(
            "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"read_file\",\"input\":{}}}",
            state);
        string firstArgumentsDelta = JsonSerializer.Serialize(new
        {
            type = "content_block_delta",
            index = 1,
            delta = new { type = "input_json_delta", partial_json = "{\"path\":\"" }
        });
        string secondArgumentsDelta = JsonSerializer.Serialize(new
        {
            type = "content_block_delta",
            index = 1,
            delta = new { type = "input_json_delta", partial_json = "a.cs\"}" }
        });
        AnthropicMessagesProvider.ExtractStreamTokens(firstArgumentsDelta, state);
        AnthropicMessagesProvider.ExtractStreamTokens(secondArgumentsDelta, state);

        IReadOnlyList<AiStreamToken> toolTokens = AnthropicMessagesProvider.ExtractStreamTokens(
            "{\"type\":\"content_block_stop\",\"index\":1}",
            state);
        AnthropicMessagesProvider.ExtractStreamTokens(
            "{\"type\":\"message_delta\",\"usage\":{\"output_tokens\":7}}",
            state);
        IReadOnlyList<AiStreamToken> stopTokens = AnthropicMessagesProvider.ExtractStreamTokens(
            "{\"type\":\"message_stop\"}",
            state);

        Assert.Single(toolTokens);
        Assert.Equal("{\"path\":\"a.cs\"}", toolTokens[0].ToolCall?.ArgumentsJson);
        Assert.Single(stopTokens);
        Assert.Equal(new AiUsageStats(10, 7, 17), stopTokens[0].UsageStats);
    }

    [Fact]
    public void WhenParsingModelsThenSelectedDiscoveredModelIsPromoted()
    {
        string json = "{\"data\":[{\"id\":\"claude-a\"},{\"id\":\"claude-b\"}]}";

        IReadOnlyList<string> models = AnthropicMessagesProvider.ParseAvailableModelsResponse(json, "claude-b");

        Assert.Equal(["claude-b", "claude-a"], models);
    }

    [Theory]
    [InlineData(null, "https://api.anthropic.com/v1/messages")]
    [InlineData("https://example.test", "https://example.test/v1/messages")]
    [InlineData("https://example.test/v1", "https://example.test/v1/messages")]
    [InlineData("https://example.test/v1/messages", "https://example.test/v1/messages")]
    [InlineData("localhost:8080", "http://localhost:8080/v1/messages")]
    public void WhenBuildingMessagesUrlThenEndpointIsNormalized(string? endpoint, string expected)
    {
        string result = AnthropicMessagesProvider.BuildMessagesUrl(endpoint);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenBuildingModelsUrlFromMessagesEndpointThenModelsEndpointIsReturned()
    {
        string result = AnthropicMessagesProvider.BuildModelsUrl("https://example.test/v1/messages");

        Assert.Equal("https://example.test/v1/models", result);
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseJson;

        public RecordingHttpMessageHandler(string? responseJson = null)
        {
            _responseJson = responseJson ??
                "{\"type\":\"message\",\"content\":[{\"type\":\"text\",\"text\":\"Hello back\"}],\"usage\":{\"input_tokens\":1,\"output_tokens\":2}}";
        }

        public Uri? RequestUri { get; private set; }

        public string ApiKey { get; private set; } = string.Empty;

        public string Authorization { get; private set; } = string.Empty;

        public string AnthropicVersionHeader { get; private set; } = string.Empty;

        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            ApiKey = request.Headers.TryGetValues("x-api-key", out IEnumerable<string>? apiKeyValues)
                ? apiKeyValues.Single()
                : string.Empty;
            Authorization = request.Headers.Authorization?.ToString() ?? string.Empty;
            AnthropicVersionHeader = request.Headers.GetValues("anthropic-version").Single();
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _responseJson,
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
