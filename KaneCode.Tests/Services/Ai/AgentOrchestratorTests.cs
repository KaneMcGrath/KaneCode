using KaneCode.Services.Ai.Agents;

namespace KaneCode.Tests.Services.Ai;

public sealed class AgentOrchestratorTests
{
    private static readonly IReadOnlyList<string> KnownProviderRefs =
        ["v1completions", "v1chatcompletions", "googlegemini"];

    [Fact]
    public void WhenModelIsProviderPrefixedByIdThenItIsSplitIntoProviderAndModel()
    {
        bool result = AgentOrchestrator.TrySplitProviderPrefixedModel(
            "v1chatcompletions/gpt-4o",
            KnownProviderRefs,
            out string providerRef,
            out string modelName);

        Assert.True(result);
        Assert.Equal("v1chatcompletions", providerRef);
        Assert.Equal("gpt-4o", modelName);
    }

    [Fact]
    public void WhenModelIsProviderPrefixedByLabelThenItIsSplitIntoProviderAndModel()
    {
        IReadOnlyList<string> refs = [.. KnownProviderRefs, "My OpenAI Key"];

        bool result = AgentOrchestrator.TrySplitProviderPrefixedModel(
            "My OpenAI Key/gpt-4o",
            refs,
            out string providerRef,
            out string modelName);

        Assert.True(result);
        Assert.Equal("My OpenAI Key", providerRef);
        Assert.Equal("gpt-4o", modelName);
    }

    [Fact]
    public void WhenLabelContainsSlashThenTheLongestMatchingReferenceWins()
    {
        IReadOnlyList<string> refs = ["openai", "openai/prod"];

        bool result = AgentOrchestrator.TrySplitProviderPrefixedModel(
            "openai/prod/gpt-4o",
            refs,
            out string providerRef,
            out string modelName);

        Assert.True(result);
        Assert.Equal("openai/prod", providerRef);
        Assert.Equal("gpt-4o", modelName);
    }

    [Fact]
    public void WhenModelPrefixMatchesLabelIgnoringCaseThenItIsSplit()
    {
        IReadOnlyList<string> refs = [.. KnownProviderRefs, "my openai key"];

        bool result = AgentOrchestrator.TrySplitProviderPrefixedModel(
            "MY OPENAI KEY/gemini-2.0-flash",
            refs,
            out string providerRef,
            out string modelName);

        Assert.True(result);
        Assert.Equal("my openai key", providerRef);
        Assert.Equal("gemini-2.0-flash", modelName);
    }

    [Fact]
    public void WhenModelHasNoSeparatorThenItIsNotSplit()
    {
        bool result = AgentOrchestrator.TrySplitProviderPrefixedModel(
            "gpt-4o",
            KnownProviderRefs,
            out string providerRef,
            out string modelName);

        Assert.False(result);
        Assert.Equal(string.Empty, providerRef);
        Assert.Equal(string.Empty, modelName);
    }

    [Fact]
    public void WhenModelPrefixIsUnknownProviderThenItIsNotSplit()
    {
        // Model IDs containing slashes (e.g. OpenRouter-style "vendor/model") must
        // pass through unchanged unless the prefix is a registered provider ID or label.
        bool result = AgentOrchestrator.TrySplitProviderPrefixedModel(
            "openai/gpt-4o",
            KnownProviderRefs,
            out string providerRef,
            out string modelName);

        Assert.False(result);
        Assert.Equal(string.Empty, providerRef);
        Assert.Equal(string.Empty, modelName);
    }

    [Fact]
    public void WhenModelEndsWithSeparatorThenItIsNotSplit()
    {
        bool result = AgentOrchestrator.TrySplitProviderPrefixedModel(
            "v1chatcompletions/",
            KnownProviderRefs,
            out string providerRef,
            out string modelName);

        Assert.False(result);
    }

    [Fact]
    public void WhenModelStartsWithSeparatorThenItIsNotSplit()
    {
        bool result = AgentOrchestrator.TrySplitProviderPrefixedModel(
            "/gpt-4o",
            KnownProviderRefs,
            out string providerRef,
            out string modelName);

        Assert.False(result);
    }

    [Fact]
    public void WhenModelIsBlankThenItIsNotSplit()
    {
        bool result = AgentOrchestrator.TrySplitProviderPrefixedModel(
            "   ",
            KnownProviderRefs,
            out string providerRef,
            out string modelName);

        Assert.False(result);
    }
}
