using KaneCode.Services.Ai;
using System.Text.Json;

namespace KaneCode.Tests.Services.Ai;

public class V1ChatCompletionsProviderTests
{
    [Fact]
    public void WhenStreamingEnabledThenRequestPayloadSetsStreamTrue()
    {
        AiProviderSettings settings = new AiProviderSettings
        {
            Endpoint = "https://example.test/v1",
            SelectedModel = "gpt-test"
        };
        using JsonDocument messagesDocument = JsonDocument.Parse("[{\"role\":\"user\",\"content\":\"Hello\"}]");

        string json = V1ChatCompletionsProvider.BuildChatCompletionRequestJson(
            "gpt-test",
            messagesDocument.RootElement.Clone(),
            default,
            settings,
            isGroqCompatibleEndpoint: false,
            streamResponse: true);

        using JsonDocument payloadDocument = JsonDocument.Parse(json);

        bool result = payloadDocument.RootElement.GetProperty("stream").GetBoolean();

        Assert.True(result);
    }

    [Fact]
    public void WhenStreamingEnabledThenRequestPayloadIncludesUsageMetadata()
    {
        AiProviderSettings settings = new AiProviderSettings
        {
            Endpoint = "https://example.test/v1",
            SelectedModel = "gpt-test"
        };
        using JsonDocument messagesDocument = JsonDocument.Parse("[{\"role\":\"user\",\"content\":\"Hello\"}]");

        string json = V1ChatCompletionsProvider.BuildChatCompletionRequestJson(
            "gpt-test",
            messagesDocument.RootElement.Clone(),
            default,
            settings,
            isGroqCompatibleEndpoint: false,
            streamResponse: true);

        using JsonDocument payloadDocument = JsonDocument.Parse(json);

        bool includeUsage = payloadDocument.RootElement
            .GetProperty("stream_options")
            .GetProperty("include_usage")
            .GetBoolean();

        Assert.True(includeUsage);
    }

    [Fact]
    public void WhenStreamingDisabledThenRequestPayloadSetsStreamFalse()
    {
        AiProviderSettings settings = new AiProviderSettings
        {
            Endpoint = "https://example.test/v1",
            SelectedModel = "gpt-test"
        };
        using JsonDocument messagesDocument = JsonDocument.Parse("[{\"role\":\"user\",\"content\":\"Hello\"}]");

        string json = V1ChatCompletionsProvider.BuildChatCompletionRequestJson(
            "gpt-test",
            messagesDocument.RootElement.Clone(),
            default,
            settings,
            isGroqCompatibleEndpoint: false,
            streamResponse: false);

        using JsonDocument payloadDocument = JsonDocument.Parse(json);

        bool result = payloadDocument.RootElement.GetProperty("stream").GetBoolean();

        Assert.False(result);
    }

    [Fact]
    public void WhenStreamingDisabledThenRequestPayloadOmitsUsageMetadata()
    {
        AiProviderSettings settings = new AiProviderSettings
        {
            Endpoint = "https://example.test/v1",
            SelectedModel = "gpt-test"
        };
        using JsonDocument messagesDocument = JsonDocument.Parse("[{\"role\":\"user\",\"content\":\"Hello\"}]");

        string json = V1ChatCompletionsProvider.BuildChatCompletionRequestJson(
            "gpt-test",
            messagesDocument.RootElement.Clone(),
            default,
            settings,
            isGroqCompatibleEndpoint: false,
            streamResponse: false);

        using JsonDocument payloadDocument = JsonDocument.Parse(json);

        Assert.False(payloadDocument.RootElement.TryGetProperty("stream_options", out JsonElement _));
    }

    [Fact]
    public void WhenParsingModelsResponseThenSelectedModelIsPromotedToTopWhenDiscovered()
    {
        string responseJson = """
            {
              "data": [
                { "id": "gpt-4.1" },
                { "id": "gpt-4o-mini" }
              ]
            }
            """;

        IReadOnlyList<string> models = V1ChatCompletionsProvider.ParseAvailableModelsResponse(responseJson, "gpt-4o");

        // "gpt-4o" is NOT in the discovered list, so it is not injected.
        // Only the discovered models are returned.
        Assert.Equal(["gpt-4.1", "gpt-4o-mini"], models);
    }

    [Fact]
    public void WhenParsingModelsResponseThenKnownSelectedModelIsFirst()
    {
        // Verify that when the selected model IS among the discovered models,
        // it is promoted to the top of the list.
        string responseJson = """
            {
              "data": [
                { "id": "gpt-4.1" },
                { "id": "gpt-4o" },
                { "id": "gpt-4o-mini" }
              ]
            }
            """;

        IReadOnlyList<string> models = V1ChatCompletionsProvider.ParseAvailableModelsResponse(responseJson, "gpt-4o");

        // "gpt-4o" is found in the discovered list and promoted to the top.
        Assert.Equal(["gpt-4o", "gpt-4.1", "gpt-4o-mini"], models);
    }

    [Fact]
    public void WhenBuildingModelsUrlFromChatCompletionsEndpointThenModelsEndpointIsReturned()
    {
        string url = V1ChatCompletionsProvider.BuildModelsUrl("https://example.test/v1/chat/completions");

        Assert.Equal("https://example.test/v1/models", url);
    }
}
