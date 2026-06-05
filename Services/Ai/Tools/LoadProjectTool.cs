using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Application mode tool that loads a project, solution, or folder by an explicit file system path.
/// Dispatches the load operation to the UI thread and waits for it to complete before returning,
/// so the AI sees the project as actually loaded when the tool succeeds.
/// </summary>
internal sealed class LoadProjectTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "path": {
                    "type": "string",
                    "description": "The file system path to the .sln file, .csproj file, or folder to open. Can be absolute or a path relative to the user's default project folder."
                }
            },
            "required": ["path"]
        }
        """).RootElement.Clone();

    private readonly Func<string, Task> _loadProjectByPathAsync;
    private readonly Func<string, Task> _loadSolutionByPathAsync;
    private readonly Action<string> _loadFolderAsync;
    private readonly Func<string> _defaultProjectFolderProvider;

    public LoadProjectTool(
        Func<string, Task> loadProjectByPathAsync,
        Func<string, Task> loadSolutionByPathAsync,
        Action<string> loadFolderAsync,
        Func<string> defaultProjectFolderProvider)
    {
        ArgumentNullException.ThrowIfNull(loadProjectByPathAsync);
        ArgumentNullException.ThrowIfNull(loadSolutionByPathAsync);
        ArgumentNullException.ThrowIfNull(loadFolderAsync);
        ArgumentNullException.ThrowIfNull(defaultProjectFolderProvider);

        _loadProjectByPathAsync = loadProjectByPathAsync;
        _loadSolutionByPathAsync = loadSolutionByPathAsync;
        _loadFolderAsync = loadFolderAsync;
        _defaultProjectFolderProvider = defaultProjectFolderProvider;
    }

    public string Name => "load_project";

    public string Category => "General";

    public string Description =>
        "Loads a .NET project (.csproj), solution (.sln), or folder into the IDE by path. " +
        "If a relative path is given, it is resolved relative to the user's default project folder. " +
        "Use this to open any project on the file system. The tool waits for the IDE to finish " +
        "loading before returning success.";

    public JsonElement ParametersSchema => Schema;

    public bool RequiresConfirmation => false;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("path", out var pathElement) ||
            string.IsNullOrWhiteSpace(pathElement.GetString()))
        {
            return ToolCallResult.Fail("Missing required parameter: path");
        }

        string inputPath = pathElement.GetString()!.Trim();

        // Resolve relative paths against the default project folder
        string fullPath;
        if (System.IO.Path.IsPathRooted(inputPath))
        {
            fullPath = System.IO.Path.GetFullPath(inputPath);
        }
        else
        {
            string defaultFolder = _defaultProjectFolderProvider();
            fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(defaultFolder, inputPath));
        }

        string displayName = System.IO.Path.GetFileName(fullPath);

        // Determine the type of load to perform
        bool isSolution = false;
        bool isProject = false;

        if (System.IO.File.Exists(fullPath))
        {
            string extension = System.IO.Path.GetExtension(fullPath);
            isSolution = extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                         extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);
            isProject = extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase);

            if (!isSolution && !isProject)
            {
                // It's a file but not a recognized project type — load the parent folder
                string? dir = System.IO.Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                {
                    fullPath = dir;
                    displayName = System.IO.Path.GetFileName(dir);
                }
                else
                {
                    return ToolCallResult.Fail(
                        $"The path '{fullPath}' is not a .sln, .csproj, or folder.");
                }
            }
        }
        else if (System.IO.Directory.Exists(fullPath))
        {
            // Folder - handled below via _loadFolderAsync
        }
        else
        {
            return ToolCallResult.Fail(
                $"The path '{fullPath}' does not exist. Check the path and try again. " +
                $"If you provided a relative path, it was resolved against the default project folder: " +
                $"{_defaultProjectFolderProvider()}");
        }

        // Dispatch the load to the UI thread and wait for it to complete.
        // The ViewModel's load methods (OpenProjectByPathAsync, OpenSolutionByPathAsync,
        // LoadProjectRoot) must run on the UI thread because they manipulate WPF collections
        // and show dialogs. We use a TaskCompletionSource to bridge the async gap.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return ToolCallResult.Fail("Cannot access the UI dispatcher to load the project.");
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            dispatcher.Invoke(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                Task? loadTask = null;

                if (isSolution)
                {
                    loadTask = _loadSolutionByPathAsync(fullPath);
                }
                else if (isProject)
                {
                    loadTask = _loadProjectByPathAsync(fullPath);
                }
                else
                {
                    _loadFolderAsync(fullPath);
                }

                if (loadTask is not null)
                {
                    // The load methods are async (they return Task). We attach a
                    // continuation to capture completion, errors, or cancellation.
                    loadTask.ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            var ex = t.Exception!.InnerException ?? t.Exception;
                            tcs.TrySetException(ex);
                        }
                        else if (t.IsCanceled)
                        {
                            tcs.TrySetCanceled();
                        }
                        else
                        {
                            tcs.TrySetResult();
                        }
                    }, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                else
                {
                    // Folder load is synchronous — signal completion immediately
                    tcs.TrySetResult();
                }
            });

            // Wait for the load to complete (with cancellation support)
            await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            string itemType = isSolution ? "solution" : isProject ? "project" : "folder";
            return ToolCallResult.Ok($"Successfully loaded {itemType}: {displayName} ({fullPath})");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ToolCallResult.Fail("Loading was cancelled.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolCallResult.Fail($"Failed to load '{displayName}': {ex.Message}");
        }
    }
}
