using KaneCode.Services.Ai;
using System.Text.Json;

namespace KaneCode.Tests.Services.Ai;

public class GoogleGenAiProviderTests
{
    [Fact]
    public void WhenCreatedThenProviderIdIsGooglegemini()
    {
        AiProviderSettings settings = new AiProviderSettings
        {
            ApiKey = "test-key",
            SelectedModel = "gemini-2.0-flash"
        };

        using GoogleGenAiProvider provider = new GoogleGenAiProvider(settings);

        Assert.Equal("googlegemini", provider.ProviderId);
    }

    [Fact]
    public void WhenCreatedWithLabelThenDisplayNameUsesLabel()
    {
        AiProviderSettings settings = new AiProviderSettings
        {
            ApiKey = "test-key",
            Label = "My Gemini Key"
        };

        using GoogleGenAiProvider provider = new GoogleGenAiProvider(settings);

        Assert.Equal("My Gemini Key", provider.DisplayName);
    }

    [Fact]
    public void WhenCreatedWithoutLabelThenDisplayNameIsDefault()
    {
        AiProviderSettings settings = new AiProviderSettings
        {
            ApiKey = "test-key",
            Label = string.Empty
        };

        using GoogleGenAiProvider provider = new GoogleGenAiProvider(settings);

        Assert.Equal("Google Gemini", provider.DisplayName);
    }

    [Fact]
    public void WhenCreatedThenSupportsImagesIsTrue()
    {
        AiProviderSettings settings = new AiProviderSettings
        {
            ApiKey = "test-key"
        };

        using GoogleGenAiProvider provider = new GoogleGenAiProvider(settings);

        Assert.True(provider.SupportsImages);
    }

    [Fact]
    public void WhenApiKeyIsProvidedThenIsConfiguredIsTrue()
    {
        AiProviderSettings settings = new AiProviderSettings
        {
            ApiKey = "test-key"
        };

        using GoogleGenAiProvider provider = new GoogleGenAiProvider(settings);

        Assert.True(provider.IsConfigured);
    }

    [Fact]
    public void WhenApiKeyIsMissingThenIsConfiguredIsFalse()
    {
        AiProviderSettings settings = new AiProviderSettings
        {
            ApiKey = string.Empty
        };

        using GoogleGenAiProvider provider = new GoogleGenAiProvider(settings);

        Assert.False(provider.IsConfigured);
    }

    [Fact]
    public void WhenCreatedThenAvailableModelsContainsDefaultModels()
    {
        AiProviderSettings settings = new AiProviderSettings
        {
            ApiKey = "test-key"
        };

        using GoogleGenAiProvider provider = new GoogleGenAiProvider(settings);

        Assert.Contains("gemini-2.0-flash", provider.AvailableModels);
        Assert.Contains("gemini-2.5-flash", provider.AvailableModels);
    }

    [Fact]
    public void WhenCreatedThenAvailableModelsAreReadOnly()
    {
        AiProviderSettings settings = new AiProviderSettings
        {
            ApiKey = "test-key"
        };

        using GoogleGenAiProvider provider = new GoogleGenAiProvider(settings);

        Assert.NotNull(provider.AvailableModels);
    }

    [Fact]
    public void WhenCreatedWithSettingsThenConstructorDoesNotThrow()
    {
        AiProviderSettings settings = new AiProviderSettings
        {
            ApiKey = "test-key"
        };

        Exception? exception = Record.Exception(() =>
        {
            using GoogleGenAiProvider provider = new GoogleGenAiProvider(settings);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void WhenDisposedThenNoExceptionThrown()
    {
        AiProviderSettings settings = new AiProviderSettings
        {
            ApiKey = "test-key"
        };

        GoogleGenAiProvider provider = new GoogleGenAiProvider(settings);

        Exception? exception = Record.Exception(() => provider.Dispose());

        Assert.Null(exception);
    }

    [Fact]
    public void WhenDisposedMultipleTimesThenNoExceptionThrown()
    {
        AiProviderSettings settings = new AiProviderSettings
        {
            ApiKey = "test-key"
        };

        GoogleGenAiProvider provider = new GoogleGenAiProvider(settings);

        Exception? exception = Record.Exception(() =>
        {
            provider.Dispose();
            provider.Dispose();
        });

        Assert.Null(exception);
    }
}
