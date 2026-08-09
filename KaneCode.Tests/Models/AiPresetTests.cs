using KaneCode.Models;
using System.Text.Json;

namespace KaneCode.Tests.Models;

public sealed class AiPresetTests
{
    [Fact]
    public void WhenClonedThenDictionariesAreDeepCopied()
    {
        AiPreset original = new()
        {
            Name = "Prototype",
            SystemPrompt = "prompt",
            AllowedTools = new HashSet<string>(StringComparer.Ordinal) { "read", "write" },
            ToolDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["edit"] = "custom description"
            },
            PinnedParameters = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
            {
                ["edit"] = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["filePath"] = JsonDocument.Parse("\"src/A.cs\"").RootElement.Clone()
                }
            },
            ToolOptions = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
            {
                ["edit"] = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["engine"] = JsonDocument.Parse("\"unified_diff\"").RootElement.Clone()
                }
            },
            HiddenParameters = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            {
                ["edit"] = new HashSet<string>(StringComparer.Ordinal) { "mode" }
            }
        };

        AiPreset clone = original.Clone();

        Assert.Equal(original.Id, clone.Id);
        Assert.Equal(original.Name, clone.Name);
        Assert.NotSame(original.AllowedTools, clone.AllowedTools);
        Assert.NotSame(original.ToolDescriptions, clone.ToolDescriptions);
        Assert.NotSame(original.PinnedParameters, clone.PinnedParameters);
        Assert.NotSame(original.ToolOptions, clone.ToolOptions);
        Assert.NotSame(original.HiddenParameters, clone.HiddenParameters);

        // Mutating the clone must not affect the original
        clone.ToolDescriptions!["edit"] = "changed";
        clone.PinnedParameters!["edit"]["filePath"] = JsonDocument.Parse("\"other.cs\"").RootElement.Clone();
        clone.ToolOptions!["edit"]["engine"] = JsonDocument.Parse("\"exact_match\"").RootElement.Clone();
        clone.AllowedTools!.Add("git_commit");
        clone.HiddenParameters!["edit"].Add("contextLines");

        Assert.Equal("custom description", original.ToolDescriptions!["edit"]);
        Assert.Equal("\"src/A.cs\"", original.PinnedParameters!["edit"]["filePath"].GetRawText());
        Assert.Equal("\"unified_diff\"", original.ToolOptions!["edit"]["engine"].GetRawText());
        Assert.DoesNotContain("git_commit", original.AllowedTools);
        Assert.DoesNotContain("contextLines", original.HiddenParameters!["edit"]);
    }

    [Fact]
    public void WhenClonedWithNullMembersThenCloneHasNullMembers()
    {
        AiPreset preset = new() { Name = "Empty" };

        AiPreset clone = preset.Clone();

        Assert.Null(clone.AllowedTools);
        Assert.Null(clone.ToolDescriptions);
        Assert.Null(clone.PinnedParameters);
        Assert.Null(clone.ToolOptions);
        Assert.Null(clone.HiddenParameters);
    }

    [Fact]
    public void WhenSerializedAndDeserializedThenNewMembersRoundTrip()
    {
        AiPreset preset = new()
        {
            Name = "Round Trip",
            ToolDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["write"] = "Write files carefully to {filePath}"
            },
            PinnedParameters = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
            {
                ["write"] = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["filePath"] = JsonDocument.Parse("\"src/out.txt\"").RootElement.Clone(),
                    ["overwrite"] = JsonDocument.Parse("true").RootElement.Clone()
                }
            },
            ToolOptions = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
            {
                ["edit"] = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["timeout"] = JsonDocument.Parse("45").RootElement.Clone()
                }
            },
            HiddenParameters = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            {
                ["write"] = new HashSet<string>(StringComparer.Ordinal) { "mode", "encoding" }
            }
        };

        string json = JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true });
        AiPreset? deserialized = JsonSerializer.Deserialize<AiPreset>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(preset.Name, deserialized!.Name);
        Assert.Equal("Write files carefully to {filePath}", deserialized.ToolDescriptions!["write"]);
        Assert.Equal("\"src/out.txt\"", deserialized.PinnedParameters!["write"]["filePath"].GetRawText());
        Assert.Equal("true", deserialized.PinnedParameters!["write"]["overwrite"].GetRawText());
        Assert.Equal("45", deserialized.ToolOptions!["edit"]["timeout"].GetRawText());
        Assert.True(deserialized.HiddenParameters!["write"].SetEquals(new[] { "mode", "encoding" }));
    }

    [Fact]
    public void WhenDeserializingV1StyleJsonThenNewMembersDefaultToNull()
    {
        const string v1Json = """
            {
              "id": "abc123",
              "name": "Legacy",
              "systemPrompt": "old prompt",
              "allowedTools": ["read", "write"]
            }
            """;

        AiPreset? preset = JsonSerializer.Deserialize<AiPreset>(v1Json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(preset);
        Assert.Equal("Legacy", preset!.Name);
        Assert.Equal("old prompt", preset.SystemPrompt);
        Assert.Equal(2, preset.AllowedTools!.Count);
        Assert.Null(preset.ToolDescriptions);
        Assert.Null(preset.PinnedParameters);
        Assert.Null(preset.ToolOptions);
        Assert.Null(preset.HiddenParameters);
    }
}
