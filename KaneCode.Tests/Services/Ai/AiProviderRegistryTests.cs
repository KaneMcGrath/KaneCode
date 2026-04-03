using KaneCode.Services.Ai;

namespace KaneCode.Tests.Services.Ai;

public class AiProviderRegistryTests
{
    [Fact]
    public void WhenNewlyCreatedThenActiveProviderIsNull()
    {
        using AiProviderRegistry registry = new AiProviderRegistry();

        Assert.Null(registry.ActiveProvider);
    }

    [Fact]
    public void WhenNewlyCreatedThenProvidersIsEmpty()
    {
        using AiProviderRegistry registry = new AiProviderRegistry();

        Assert.Empty(registry.Providers);
    }

    [Fact]
    public void WhenSetActiveProviderCalledThenActiveProviderIsUpdated()
    {
        using AiProviderRegistry registry = new AiProviderRegistry();

        // SetActiveProvider accepts null to clear the active provider
        registry.SetActiveProvider(null);

        Assert.Null(registry.ActiveProvider);
    }

    [Fact]
    public void WhenGetSettingsCalledWithNullThenThrowsArgumentNullException()
    {
        using AiProviderRegistry registry = new AiProviderRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.GetSettings(null!));
    }

    [Fact]
    public void WhenDisposeCalledMultipleTimesThenDoesNotThrow()
    {
        AiProviderRegistry registry = new AiProviderRegistry();

        Exception? exception = Record.Exception(() =>
        {
            registry.Dispose();
            registry.Dispose();
        });

        Assert.Null(exception);
    }

    [Fact]
    public void WhenReloadCalledThenProvidersChangedEventIsRaised()
    {
        using AiProviderRegistry registry = new AiProviderRegistry();
        int raisedCount = 0;

        registry.ProvidersChanged += (_, _) => raisedCount++;

        registry.Reload();

        Assert.Equal(1, raisedCount);
    }
}
