using KaneCode.Services.Ai;
using System.Text.Json;

namespace KaneCode.Tests.Services.Ai;

public class AgentToolArgumentsParserTests
{
    [Fact]
    public void WhenReadFileArgumentsContainConsecutiveObjectsThenParserNormalizesToFilePathsArray()
    {
        using JsonDocument document = AgentToolArgumentsParser.Parse(
            "read_file",
            "{\"filePath\":\"MainWindow.xaml\"}{\"filePath\":\"MainWindow.xaml.cs\"}");

        JsonElement filePaths = document.RootElement.GetProperty("filePaths");
        Assert.Equal(JsonValueKind.Array, filePaths.ValueKind);
        Assert.Equal("MainWindow.xaml", filePaths[0].GetString());
        Assert.Equal("MainWindow.xaml.cs", filePaths[1].GetString());
    }

    [Fact]
    public void WhenNonReadFileArgumentsContainConsecutiveObjectsThenParserThrowsJsonException()
    {
        Assert.ThrowsAny<JsonException>(() => AgentToolArgumentsParser.Parse(
            "search_files",
            "{\"query\":\"first\"}{\"query\":\"second\"}"));
    }
}
