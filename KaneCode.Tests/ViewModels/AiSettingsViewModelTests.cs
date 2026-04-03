using KaneCode.Services.Ai;
using KaneCode.ViewModels;

namespace KaneCode.Tests.ViewModels;

public class AiSettingsViewModelTests
{
    [Fact]
    public void WhenSettingsContainsContextLengthThenEntryFormatsIt()
    {
        AiProviderSettings settings = new()
        {
            ProviderId = "openai",
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
            ProviderId = "openai",
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
            ProviderId = "openai",
            Label = "Test Provider"
        })
        {
            ContextLength = "abc"
        };

        AiProviderSettings settings = entry.ToSettings();

        Assert.Null(settings.ContextLength);
    }
}
