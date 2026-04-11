using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Tools;
using System.IO;
using System.Text.Json;

namespace KaneCode.Tests.Services.Ai.Tools;

public sealed class SearchFilesToolTests : IDisposable
{
    private readonly string _projectRoot;

    public SearchFilesToolTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), $"KaneCodeSearchFiles_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_projectRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task WhenDirectoryIsInsideAttachedExternalFolderThenSearchesFiles()
    {
        string externalDirectory = Path.Combine(Path.GetTempPath(), $"KaneCodeExternalSearch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(externalDirectory);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(externalDirectory, "notes.txt"), "hello external context");
            ExternalContextDirectoryRegistry registry = new();
            registry.SetAllowedDirectories([externalDirectory]);
            SearchFilesTool tool = new SearchFilesTool(() => _projectRoot, registry);
            JsonElement arguments = JsonDocument.Parse($"{{\"query\":\"external\",\"directory\":\"{externalDirectory.Replace("\\", "\\\\", StringComparison.Ordinal)}\"}}").RootElement;

            ToolCallResult result = await tool.ExecuteAsync(arguments);

            Assert.True(result.Success);
            Assert.Contains("notes.txt:1: hello external context", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(externalDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
