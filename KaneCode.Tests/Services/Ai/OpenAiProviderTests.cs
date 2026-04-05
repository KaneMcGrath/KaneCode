using KaneCode.Services.Ai;
using System.Text.Json;

namespace KaneCode.Tests.Services.Ai;

public class OpenAiProviderTests
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

        string json = OpenAiProvider.BuildChatCompletionRequestJson(
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
    public void WhenStreamingDisabledThenRequestPayloadSetsStreamFalse()
    {
        AiProviderSettings settings = new AiProviderSettings
        {
            Endpoint = "https://example.test/v1",
            SelectedModel = "gpt-test"
        };
        using JsonDocument messagesDocument = JsonDocument.Parse("[{\"role\":\"user\",\"content\":\"Hello\"}]");

        string json = OpenAiProvider.BuildChatCompletionRequestJson(
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
    public void WhenParsingModelsResponseThenSelectedModelIsIncludedBeforeDiscoveredModels()
    {
        string responseJson = """
            {
              "data": [
                { "id": "gpt-4.1" },
                { "id": "gpt-4o-mini" }
              ]
            }
            """;

        IReadOnlyList<string> models = OpenAiProvider.ParseAvailableModelsResponse(responseJson, "gpt-4o");

        Assert.Equal(["gpt-4o", "gpt-4.1", "gpt-4o-mini"], models);
    }

    [Fact]
    public void WhenBuildingModelsUrlFromChatCompletionsEndpointThenModelsEndpointIsReturned()
    {
        string url = OpenAiProvider.BuildModelsUrl("https://example.test/v1/chat/completions");

        Assert.Equal("https://example.test/v1/models", url);
    }
}
