using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Tools;
using System.IO;
using System.Text.Json;

namespace KaneCode.Tests.Services.Ai.Tools;

public sealed class ListFilesToolTests : IDisposable
{
    private readonly string _projectRoot;

    public ListFilesToolTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), $"KaneCodeListFiles_{Guid.NewGuid():N}");
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
    public async Task WhenDirectoryIsInsideAttachedExternalFolderThenListsFiles()
    {
        string externalDirectory = Path.Combine(Path.GetTempPath(), $"KaneCodeExternalList_{Guid.NewGuid():N}");
        Directory.CreateDirectory(externalDirectory);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(externalDirectory, "notes.txt"), "hello");
            ExternalContextDirectoryRegistry registry = new();
            registry.SetAllowedDirectories([externalDirectory]);
            ListFilesTool tool = new ListFilesTool(() => _projectRoot, registry);
            JsonElement arguments = JsonDocument.Parse($"{{\"directory\":\"{externalDirectory.Replace("\\", "\\\\", StringComparison.Ordinal)}\"}}").RootElement;

            ToolCallResult result = await tool.ExecuteAsync(arguments);

            Assert.True(result.Success);
            Assert.Contains("notes.txt", result.Output, StringComparison.Ordinal);
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
