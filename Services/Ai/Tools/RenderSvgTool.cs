using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using Svg;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that renders an SVG document to a raster image (PNG or JPEG), saves
/// it to a file inside the loaded project, and shows the rendered image inline in
/// the chat (via <see cref="ToolCallResult.SvgContent"/>). This lets agents create
/// graphics that require rasterized formats (PNG/JPEG) — UI assets, icons, diagram
/// screenshots, or images to embed in documents — rather than vector SVG.
/// </summary>
internal sealed class RenderSvgTool : IAgentTool
{
    private const int DefaultWidth = 800;
    private const int MinWidth = 16;
    private const int MaxWidth = 4096;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "filePath": {
                    "type": "string",
                    "description": "The path to save the rasterized image to. Can be absolute or relative to the loaded project root, but must stay inside the loaded project. The file extension selects the output format: .png for PNG, .jpg or .jpeg for JPEG."
                },
                "content": {
                    "type": "string",
                    "description": "The full SVG markup content. Must be valid SVG XML."
                },
                "format": {
                    "type": "string",
                    "enum": ["png", "jpeg"],
                    "description": "Optional output format override: 'png' or 'jpeg'. Defaults to the file extension of filePath. When both are given they must agree."
                },
                "width": {
                    "type": "integer",
                    "minimum": 16,
                    "maximum": 4096,
                    "description": "Optional render width in pixels (default 800, max 4096). The height is scaled automatically to preserve the SVG's aspect ratio."
                }
            },
            "required": ["filePath", "content"]
        }
        """).RootElement.Clone();

    private static readonly JsonElement BackendOptions = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "require_confirmation": {
                    "type": "boolean",
                    "default": true,
                    "description": "Require user confirmation before writing the image file"
                },
                "timeout": {
                    "type": "integer",
                    "default": 30,
                    "minimum": 1,
                    "maximum": 600,
                    "description": "Execution timeout in seconds"
                },
                "path_scope": {
                    "type": "string",
                    "enum": ["project", "project_external"],
                    "default": "project",
                    "description": "Where the output file path may resolve: project only, or project plus attached external context folders"
                }
            }
        }
        """).RootElement.Clone();

    private static readonly JsonElement DefaultOptions = JsonDocument.Parse("""
        {
            "require_confirmation": true,
            "timeout": 30,
            "path_scope": "project"
        }
        """).RootElement.Clone();

    private readonly Func<string?> _projectRootProvider;
    private readonly Action<string>? _onFileChanged;
    private readonly ExternalContextDirectoryRegistry? _externalContextDirectoryRegistry;

    public RenderSvgTool(
        Func<string?> projectRootProvider,
        Action<string>? onFileChanged = null,
        ExternalContextDirectoryRegistry? externalContextDirectoryRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _projectRootProvider = projectRootProvider;
        _onFileChanged = onFileChanged;
        _externalContextDirectoryRegistry = externalContextDirectoryRegistry;
    }

    public string Name => "render_svg";

    public string Category => "Drawing";

    public string Description =>
        "Renders an SVG document to a raster image (PNG or JPEG) and saves it to a file " +
        "inside the loaded project. Provide the target filePath (the extension selects the " +
        "format: .png, .jpg, or .jpeg) and the SVG content. The rendered image is also " +
        "displayed inline in the chat so both the agent and the user can see the result. " +
        "Use this when a graphic needs a rasterized format rather than vector SVG — for " +
        "example UI assets, icons, diagram screenshots, or images to embed in documents. " +
        "Note: JPEG has no transparency, so transparent SVG areas are composited onto " +
        "white; add an opaque background rectangle to the SVG if a different background " +
        "is wanted.";

    public JsonElement ParametersSchema => Schema;

    public JsonElement BackendOptionsSchema => BackendOptions;

    public IReadOnlyDictionary<string, JsonElement> DefaultBackendOptions
    {
        get
        {
            Dictionary<string, JsonElement> defaults = new(StringComparer.Ordinal);
            foreach (JsonProperty property in DefaultOptions.EnumerateObject())
            {
                defaults[property.Name] = property.Value.Clone();
            }

            return defaults;
        }
    }

    public bool RequiresConfirmation => true;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("filePath", out var filePathElement) ||
            string.IsNullOrWhiteSpace(filePathElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: filePath"));
        }

        if (!arguments.TryGetProperty("content", out var contentElement))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: content"));
        }

        string filePathInput = filePathElement.GetString()!.Trim();
        string content = contentElement.GetString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult(ToolCallResult.Fail("SVG content cannot be empty."));
        }

        // Parse (and thereby validate) the SVG markup before doing any work.
        SvgDocument svgDoc;
        try
        {
            using var svgStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            svgDoc = SvgDocument.Open<SvgDocument>(svgStream);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Invalid SVG content: {ex.Message}"));
        }

        // Resolve the output path inside the loaded project (or an attached external
        // context folder when the path_scope backend option permits it).
        string resolvedPath;
        string pathScope = AgentToolContext.GetString("path_scope", "project");

        try
        {
            resolvedPath = pathScope == "project_external" && _externalContextDirectoryRegistry is not null
                ? AgentToolPathResolver.ResolvePath(
                    _projectRootProvider,
                    filePathInput,
                    _externalContextDirectoryRegistry.GetAllowedDirectories())
                : AgentToolPathResolver.ResolvePath(_projectRootProvider, filePathInput);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }

        // Determine the output format from the file extension, with an optional
        // explicit format override. The two must agree when both are present.
        string? formatOverride = arguments.TryGetProperty("format", out var formatElement)
            ? formatElement.GetString()?.Trim().ToLowerInvariant()
            : null;

        string extension = Path.GetExtension(resolvedPath).ToLowerInvariant();
        string? formatFromExtension = extension switch
        {
            ".png" => "png",
            ".jpg" or ".jpeg" => "jpeg",
            _ => null
        };

        string format;
        if (formatFromExtension is not null)
        {
            if (formatOverride is not null && formatOverride != formatFromExtension)
            {
                return Task.FromResult(ToolCallResult.Fail(
                    $"filePath ends with '{extension}' but format is '{formatOverride}'. " +
                    "Make the file extension match the requested format."));
            }

            format = formatFromExtension;
        }
        else if (formatOverride is "png" or "jpeg")
        {
            format = formatOverride;
        }
        else
        {
            return Task.FromResult(ToolCallResult.Fail(
                "Could not determine the output format. Use a .png, .jpg, or .jpeg file " +
                "extension, or pass the format parameter (\"png\" or \"jpeg\")."));
        }

        int width = DefaultWidth;
        if (arguments.TryGetProperty("width", out var widthElement) &&
            widthElement.TryGetInt32(out int requestedWidth))
        {
            width = Math.Clamp(requestedWidth, MinWidth, MaxWidth);
        }

        try
        {
            string? directory = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using System.Drawing.Bitmap bitmap = svgDoc.Draw(width, 0);

            if (format == "jpeg")
            {
                // JPEG has no alpha channel: composite the render onto a white
                // background so transparent SVG areas become white instead of black.
                using var flattened = new System.Drawing.Bitmap(
                    bitmap.Width,
                    bitmap.Height,
                    PixelFormat.Format24bppRgb);
                using (Graphics graphics = Graphics.FromImage(flattened))
                {
                    graphics.Clear(Color.White);
                    graphics.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
                }

                flattened.Save(resolvedPath, ImageFormat.Jpeg);
            }
            else
            {
                bitmap.Save(resolvedPath, ImageFormat.Png);
            }

            _onFileChanged?.Invoke(resolvedPath);

            long fileSize = new FileInfo(resolvedPath).Length;
            string? projectRoot = GetProjectRootSafely();
            string displayPath = ToolResultDetails.GetDisplayPath(resolvedPath, projectRoot);
            string formatName = format == "jpeg" ? "JPEG" : "PNG";

            string output = $"Rendered SVG to '{displayPath}' as {formatName} ({bitmap.Width}×{bitmap.Height}).";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Saved '{displayPath}'");
            sb.AppendLine($"{formatName} · {bitmap.Width}×{bitmap.Height} px · {ToolResultDetails.FormatByteSize(fileSize)}");

            // Carry the SVG content so the chat panel renders the image inline,
            // exactly like draw_svg does.
            return Task.FromResult(ToolCallResult.OkWithSvg(output, content));
        }
        catch (IOException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"IO error rendering SVG: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Access denied: {ex.Message}"));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Invalid path or image data: {ex.Message}"));
        }
        catch (NotSupportedException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Unsupported path or format: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Failed to render SVG: {ex.Message}"));
        }
    }

    /// <summary>
    /// Resolves the project root for display purposes, or null when no project
    /// is loaded (e.g. direct tool invocation in tests). Never throws.
    /// </summary>
    private string? GetProjectRootSafely()
    {
        try
        {
            return AgentToolPathResolver.GetProjectRootDirectory(_projectRootProvider);
        }
        catch
        {
            return null;
        }
    }
}
