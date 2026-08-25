using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Tools;

namespace KaneCode.Tests.Services.Ai.Tools;

public sealed class RenderSvgToolTests : IDisposable
{
    private const string SimpleSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\">" +
        "<rect width=\"100\" height=\"50\" fill=\"#4a90d9\"/></svg>";

    private readonly string _tempDir;

    public RenderSvgToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"KaneCodeRenderSvgTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task WhenValidSvgPngThenFileIsCreatedAndSvgContentReturned()
    {
        RenderSvgTool tool = new RenderSvgTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""
            {
              "filePath": "images/diagram.png",
              "content": "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='50'><rect width='100' height='50' fill='red'/></svg>"
            }
            """).RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);
        string writtenPath = Path.Combine(_tempDir, "images", "diagram.png");

        Assert.True(result.Success);
        Assert.True(File.Exists(writtenPath));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ReadHeader(writtenPath, 8));

        // The SVG content is carried back so the chat panel renders it inline,
        // exactly like draw_svg.
        Assert.Equal(
            "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='50'><rect width='100' height='50' fill='red'/></svg>",
            result.SvgContent);
        Assert.Contains("diagram.png", result.Output, StringComparison.Ordinal);
        Assert.Contains("PNG", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenValidSvgJpegThenFileIsCreatedWithJpegSignature()
    {
        RenderSvgTool tool = new RenderSvgTool(() => _tempDir);
        JsonElement args = BuildArgs("photo.jpg", SimpleSvg);

        ToolCallResult result = await tool.ExecuteAsync(args);
        string writtenPath = Path.Combine(_tempDir, "photo.jpg");

        Assert.True(result.Success);
        Assert.True(File.Exists(writtenPath));

        byte[] header = ReadHeader(writtenPath, 3);
        Assert.Equal(0xFF, header[0]);
        Assert.Equal(0xD8, header[1]);
        Assert.Equal(0xFF, header[2]);
        Assert.Contains("JPEG", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenJpegThenTransparentBackgroundIsFlattenedToWhite()
    {
        // A small rectangle that does not fill the canvas, leaving transparent
        // margins around it.
        const string partialSvg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\">" +
            "<rect x=\"10\" y=\"10\" width=\"40\" height=\"30\" fill=\"#4a90d9\"/></svg>";

        RenderSvgTool tool = new RenderSvgTool(() => _tempDir);
        JsonElement args = BuildArgs("photo.jpg", partialSvg);

        ToolCallResult result = await tool.ExecuteAsync(args);
        string writtenPath = Path.Combine(_tempDir, "photo.jpg");

        Assert.True(result.Success);

        // The SVG's transparent margin must be composited onto white (JPEG has no
        // alpha channel) rather than left black.
        using System.Drawing.Bitmap bitmap = new(writtenPath);
        System.Drawing.Color corner = bitmap.GetPixel(0, 0);
        Assert.Equal(255, corner.R);
        Assert.Equal(255, corner.G);
        Assert.Equal(255, corner.B);
    }

    [Fact]
    public async Task WhenWidthProvidedThenRenderedBitmapMatchesWidthAndAspectRatio()
    {
        RenderSvgTool tool = new RenderSvgTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""
            {
              "filePath": "wide.png",
              "content": "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='50'><rect width='100' height='50' fill='red'/></svg>",
              "width": 200
            }
            """).RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);
        string writtenPath = Path.Combine(_tempDir, "wide.png");

        Assert.True(result.Success);

        using System.Drawing.Bitmap bitmap = new(writtenPath);
        Assert.Equal(200, bitmap.Width);
        Assert.Equal(100, bitmap.Height); // 2:1 aspect ratio preserved
        Assert.Contains("200×100", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenOverwritingExistingFileThenSucceeds()
    {
        string path = Path.Combine(_tempDir, "existing.png");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        RenderSvgTool tool = new RenderSvgTool(() => _tempDir);
        JsonElement args = BuildArgs("existing.png", SimpleSvg);

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ReadHeader(path, 8));
    }

    [Fact]
    public async Task WhenSvgIsInvalidThenReturnsFailureAndWritesNothing()
    {
        RenderSvgTool tool = new RenderSvgTool(() => _tempDir);
        JsonElement args = BuildArgs("broken.png", "this is not svg");

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("Invalid SVG", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_tempDir, "broken.png")));
    }

    [Fact]
    public async Task WhenPathIsOutsideProjectThenReturnsFailureAndWritesNothing()
    {
        string outsideDirectory = Path.Combine(Path.GetTempPath(), $"KaneCodeOutside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);

        try
        {
            string outsideFilePath = Path.Combine(outsideDirectory, "outside.png");
            RenderSvgTool tool = new RenderSvgTool(() => _tempDir);
            JsonElement args = BuildArgs(outsideFilePath, SimpleSvg);

            ToolCallResult result = await tool.ExecuteAsync(args);

            Assert.False(result.Success);
            Assert.Contains("inside the loaded project", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outsideFilePath));
        }
        finally
        {
            try { Directory.Delete(outsideDirectory, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task WhenExtensionUnknownAndNoFormatThenReturnsFailure()
    {
        RenderSvgTool tool = new RenderSvgTool(() => _tempDir);
        JsonElement args = BuildArgs("output.xyz", SimpleSvg);

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("Could not determine the output format", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_tempDir, "output.xyz")));
    }

    [Fact]
    public async Task WhenFormatProvidedWithUnknownExtensionThenWritesRequestedFormat()
    {
        RenderSvgTool tool = new RenderSvgTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""
            {
              "filePath": "output.bin",
              "content": "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='50'><rect width='100' height='50' fill='red'/></svg>",
              "format": "png"
            }
            """).RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);
        string writtenPath = Path.Combine(_tempDir, "output.bin");

        Assert.True(result.Success);
        Assert.True(File.Exists(writtenPath));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ReadHeader(writtenPath, 8));
    }

    [Fact]
    public async Task WhenFormatAndExtensionMismatchThenReturnsFailure()
    {
        RenderSvgTool tool = new RenderSvgTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""
            {
              "filePath": "photo.png",
              "content": "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='50'><rect width='100' height='50' fill='red'/></svg>",
              "format": "jpeg"
            }
            """).RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("Make the file extension match", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_tempDir, "photo.png")));
    }

    [Fact]
    public async Task WhenRequiredParametersMissingThenReturnsFailure()
    {
        RenderSvgTool tool = new RenderSvgTool(() => _tempDir);

        ToolCallResult noPath = await tool.ExecuteAsync(JsonDocument.Parse(
            """{ "content": "<svg/>" }""").RootElement);
        Assert.False(noPath.Success);
        Assert.Contains("filePath", noPath.Error, StringComparison.OrdinalIgnoreCase);

        ToolCallResult noContent = await tool.ExecuteAsync(JsonDocument.Parse(
            """{ "filePath": "out.png" }""").RootElement);
        Assert.False(noContent.Success);
        Assert.Contains("content", noContent.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MetadataExposesNameCategoryAndRequiresConfirmation()
    {
        RenderSvgTool tool = new RenderSvgTool(() => _tempDir);

        Assert.Equal("render_svg", tool.Name);
        Assert.Equal("Drawing", tool.Category);
        Assert.True(tool.RequiresConfirmation);
        Assert.Contains("PNG", tool.Description, StringComparison.Ordinal);
        Assert.Contains("JPEG", tool.Description, StringComparison.Ordinal);
        Assert.Contains("filePath", tool.Description, StringComparison.Ordinal);
    }

    private static JsonElement BuildArgs(string filePath, string content)
    {
        string escapedPath = filePath.Replace("\\", "\\\\", StringComparison.Ordinal);
        string escapedContent = content.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return JsonDocument.Parse($"{{\"filePath\":\"{escapedPath}\",\"content\":\"{escapedContent}\"}}")
            .RootElement;
    }

    private static byte[] ReadHeader(string path, int count)
    {
        byte[] header = new byte[count];
        using FileStream stream = File.OpenRead(path);
        stream.ReadExactly(header, 0, count);
        return header;
    }
}
