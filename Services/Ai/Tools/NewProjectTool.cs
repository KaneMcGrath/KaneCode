using System.Text.Json;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Utils;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Application mode tool that creates a new .NET project from a template
/// and loads it into the IDE. Waits for both creation and loading to complete
/// before returning so the AI sees the project as actually loaded.
/// </summary>
internal sealed class NewProjectTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "name": {
                    "type": "string",
                    "description": "The name of the new project. This will be used as the project name and directory name."
                },
                "template": {
                    "type": "string",
                    "description": "The template short name to use. Common options: 'console', 'classlib', 'wpf', 'winforms', 'web', 'webapi', 'mvc', 'blazorwasm', 'blazorserver', 'xunit', 'nunit', 'mstest'. Defaults to 'console' if not specified."
                },
                "targetFramework": {
                    "type": "string",
                    "description": "The target framework moniker (e.g. 'net8.0', 'net9.0'). If not specified, the template's default is used."
                },
                "createSolution": {
                    "type": "boolean",
                    "description": "Whether to create a solution (.sln) file for the project. Defaults to true."
                },
                "directory": {
                    "type": "string",
                    "description": "Optional parent directory for the project. If not specified, the user's default project folder is used."
                }
            },
            "required": ["name"]
        }
        """).RootElement.Clone();

    private readonly TemplateEngineService _templateEngine;
    private readonly Func<string, Task> _loadProjectByPathAsync;
    private readonly Func<string, Task> _loadSolutionByPathAsync;
    private readonly Func<string> _defaultProjectFolderProvider;

    public NewProjectTool(
        TemplateEngineService templateEngine,
        Func<string, Task> loadProjectByPathAsync,
        Func<string, Task> loadSolutionByPathAsync,
        Func<string> defaultProjectFolderProvider)
    {
        ArgumentNullException.ThrowIfNull(templateEngine);
        ArgumentNullException.ThrowIfNull(loadProjectByPathAsync);
        ArgumentNullException.ThrowIfNull(loadSolutionByPathAsync);
        ArgumentNullException.ThrowIfNull(defaultProjectFolderProvider);

        _templateEngine = templateEngine;
        _loadProjectByPathAsync = loadProjectByPathAsync;
        _loadSolutionByPathAsync = loadSolutionByPathAsync;
        _defaultProjectFolderProvider = defaultProjectFolderProvider;
    }

    public string Name => "new_project";

    public string Category => "General";

    public string Description =>
        "Creates a new .NET project from an SDK template and loads it into the IDE. " +
        "Supports console apps, class libraries, WPF, WinForms, web, test projects, and more. " +
        "Provide a project name and optionally a template short name (defaults to 'console'). " +
        "The tool waits for the project to be created and loaded before returning success.";

    public JsonElement ParametersSchema => Schema;

    public bool RequiresConfirmation => true;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("name", out var nameElement) ||
            string.IsNullOrWhiteSpace(nameElement.GetString()))
        {
            return ToolCallResult.Fail("Missing required parameter: name");
        }

        string projectName = nameElement.GetString()!.Trim();
        string template = "console";
        string? targetFramework = null;
        bool createSolution = true;
        string? directory = null;

        if (arguments.TryGetProperty("template", out var templateElement) &&
            templateElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(templateElement.GetString()))
        {
            template = templateElement.GetString()!.Trim().ToLowerInvariant();
        }

        if (arguments.TryGetProperty("targetFramework", out var tfmElement) &&
            tfmElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(tfmElement.GetString()))
        {
            targetFramework = tfmElement.GetString()!.Trim();
        }

        if (arguments.TryGetProperty("createSolution", out var slnElement) &&
            slnElement.ValueKind == JsonValueKind.False)
        {
            createSolution = false;
        }

        if (arguments.TryGetProperty("directory", out var dirElement) &&
            dirElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(dirElement.GetString()))
        {
            directory = dirElement.GetString()!.Trim();
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = _defaultProjectFolderProvider();
        }

        if (string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory))
        {
            return ToolCallResult.Fail(
                "The destination directory does not exist. Specify a valid directory or configure a default project folder in Options.");
        }

        var projectDir = System.IO.Path.Combine(directory, projectName);

        // ── Step 1: Find the template (can be done from background thread) ──
        IReadOnlyList<ITemplateInfo> templates;
        try
        {
            templates = await _templateEngine.GetProjectTemplatesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToolCallResult.Fail($"Could not discover SDK templates: {ex.Message}");
        }

        var templateInfo = templates.FirstOrDefault(t =>
            t.ShortNameList.Any(sn => sn.Equals(template, StringComparison.OrdinalIgnoreCase)));

        if (templateInfo is null)
        {
            // Try partial match
            templateInfo = templates.FirstOrDefault(t =>
                t.Name.Contains(template, StringComparison.OrdinalIgnoreCase) ||
                t.ShortNameList.Any(sn => sn.Contains(template, StringComparison.OrdinalIgnoreCase)));
        }

        if (templateInfo is null)
        {
            var shortNames = string.Join(", ",
                templates.Where(t => string.Equals(t.GetTemplateType(), "project", StringComparison.OrdinalIgnoreCase))
                         .SelectMany(t => t.ShortNameList)
                         .Distinct()
                         .OrderBy(n => n)
                         .Take(30));

            return ToolCallResult.Fail(
                $"Template '{template}' not found. Available templates: {shortNames}");
        }

        // ── Step 2: Create the project files (I/O bound, can be done from background) ──
        try
        {
            await _templateEngine.CreateProjectAsync(
                templateInfo, projectName, projectDir, targetFramework, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToolCallResult.Fail($"Failed to create project: {ex.Message}");
        }

        // ── Step 3: Load the project/solution into the IDE (must be on the UI thread) ──
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

                Task? loadTask;

                if (createSolution)
                {
                    // Solution was already created by CreateProjectAsync above.
                    // Now CreateSolutionAsync creates the .sln file.
                    var solutionPathTask = _templateEngine.CreateSolutionAsync(projectName, projectDir);

                    solutionPathTask.ContinueWith(st =>
                    {
                        if (st.IsFaulted)
                        {
                            tcs.TrySetException(st.Exception!.InnerException ?? st.Exception);
                            return;
                        }

                        if (st.IsCanceled)
                        {
                            tcs.TrySetCanceled();
                            return;
                        }

                        string solutionPath = st.Result;
                        loadTask = _loadSolutionByPathAsync(solutionPath);
                        loadTask.ContinueWith(lt =>
                        {
                            if (lt.IsFaulted)
                                tcs.TrySetException(lt.Exception!.InnerException ?? lt.Exception);
                            else if (lt.IsCanceled)
                                tcs.TrySetCanceled();
                            else
                                tcs.TrySetResult();
                        }, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                else
                {
                    var csprojFiles = System.IO.Directory.EnumerateFiles(projectDir, "*.csproj").ToArray();
                    var csprojPath = csprojFiles.Length > 0 ? csprojFiles[0] : null;

                    if (string.IsNullOrEmpty(csprojPath))
                    {
                        tcs.TrySetException(new InvalidOperationException(
                            "Project was created but no .csproj file was found."));
                        return;
                    }

                    loadTask = _loadProjectByPathAsync(csprojPath);
                    loadTask.ContinueWith(lt =>
                    {
                        if (lt.IsFaulted)
                            tcs.TrySetException(lt.Exception!.InnerException ?? lt.Exception);
                        else if (lt.IsCanceled)
                            tcs.TrySetCanceled();
                        else
                            tcs.TrySetResult();
                    }, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            });

            // Wait for the entire load to complete
            await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            return ToolCallResult.Ok(
                $"Successfully created and loaded {template} project '{projectName}' " +
                $"in {(createSolution ? "solution" : "project")} mode.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ToolCallResult.Fail("Project creation was cancelled.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolCallResult.Fail($"Failed to load created project: {ex.Message}");
        }
    }
}
