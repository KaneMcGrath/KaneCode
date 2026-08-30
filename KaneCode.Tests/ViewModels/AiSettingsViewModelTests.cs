using KaneCode.Services.Ai;
using KaneCode.ViewModels;

namespace KaneCode.Tests.ViewModels;

public class AiSettingsViewModelTests
{
    [Fact]
    public void WhenMappingAnthropicProviderThenDisplayNameAndIdRoundTrip()
    {
        Assert.Contains("/v1/messages", AiSettingsViewModel.ProviderTypes);
        Assert.Equal("anthropicmessages", AiSettingsViewModel.DisplayToProviderId("/v1/messages"));
        Assert.Equal("/v1/messages", AiSettingsViewModel.ProviderIdToDisplay("anthropicmessages"));
    }

    [Fact]
    public void WhenAnthropicMaxOutputTokensIsSetThenItRoundTrips()
    {
        AiProviderEntryViewModel entry = new(new AiProviderSettings
        {
            ProviderId = "anthropicmessages",
            MaxOutputTokens = 2048
        });

        AiProviderSettings settings = entry.ToSettings();

        Assert.True(entry.IsMaxOutputTokensVisible);
        Assert.False(entry.IsMinPVisible);
        Assert.False(entry.IsPresencePenaltyVisible);
        Assert.False(entry.IsRepetitionPenaltyVisible);
        Assert.Equal("2048", entry.MaxOutputTokens);
        Assert.Equal(2048, settings.MaxOutputTokens);
    }

    [Fact]
    public void WhenSettingsContainsContextLengthThenEntryFormatsIt()
    {
        AiProviderSettings settings = new()
        {
            ProviderId = "v1completions",
            Label = "Test Provider",
            ContextLength = 16384
        };

        AiProviderEntryViewModel entry = new(settings);

        Assert.Equal("16384", entry.ContextLength);
    }

    [Fact]
    public void WhenContextLengthIsNumericThenToSettingsParsesIt()
    {
        AiProviderEntryViewModel entry = new(new AiProviderSettings
        {
            ProviderId = "v1completions",
            Label = "Test Provider"
        })
        {
            ContextLength = "24576"
        };

        AiProviderSettings settings = entry.ToSettings();

        Assert.Equal(24576, settings.ContextLength);
    }

    [Fact]
    public void WhenContextLengthIsInvalidThenToSettingsReturnsNull()
    {
        AiProviderEntryViewModel entry = new(new AiProviderSettings
        {
            ProviderId = "v1completions",
            Label = "Test Provider"
        })
        {
            ContextLength = "abc"
        };

        AiProviderSettings settings = entry.ToSettings();

        Assert.Null(settings.ContextLength);
    }

    [Fact]
    public void WhenInferenceParameterIsMissingThenEntryShowsDefaultValueButKeepsItDisabled()
    {
        AiProviderSettings settings = new()
        {
            ProviderId = "v1completions",
            Label = "Test Provider",
            Temperature = null
        };

        AiProviderEntryViewModel entry = new(settings);

        Assert.Equal("0.6", entry.Temperature);
        Assert.False(entry.IsTemperatureEnabled);
    }

    [Fact]
    public void WhenInferenceParameterIsDisabledThenToSettingsOmitsIt()
    {
        AiProviderEntryViewModel entry = new(new AiProviderSettings
        {
            ProviderId = "v1completions",
            Label = "Test Provider",
            Temperature = 0.9
        })
        {
            IsTemperatureEnabled = false,
            Temperature = "0.9"
        };

        AiProviderSettings settings = entry.ToSettings();

        Assert.Null(settings.Temperature);
    }
}
