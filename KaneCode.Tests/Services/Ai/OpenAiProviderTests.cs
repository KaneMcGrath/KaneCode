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
}
