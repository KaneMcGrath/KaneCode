using KaneCode.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.IO;
using System.Linq;
using System.Text;

namespace KaneCode.Services.Ai;

/// <summary>
/// Creates AI chat references for built-in, project, and external context sources.
/// </summary>
internal static class AiContextReferenceFactory
{
    private const int MaxBuildOutputLines = 250;
    private const int MaxExternalFolderFiles = 400;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea",
        "bin", "obj",
        "node_modules", ".npm",
        "packages", ".nuget",
        "__pycache__", ".venv", "venv",
    };

    internal static AiChatReference CreateFileReference(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        AiChatReference reference = new(AiReferenceKind.File, filePath);

        try
        {
            reference.Content = File.ReadAllText(filePath);
        }
        catch (IOException)
        {
            reference.Content = "(unable to read file)";
        }
        catch (UnauthorizedAccessException)
        {
            reference.Content = "(unable to read file)";
        }

        return reference;
    }

    internal static AiChatReference CreateCurrentDocumentReference(AiContextDocumentSnapshot document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new AiChatReference(AiReferenceKind.CurrentDocument, document.FilePath, document.DisplayName)
        {
            Content = document.Content
        };
    }

    internal static AiChatReference CreateAllOpenDocumentsReference(IReadOnlyList<AiContextDocumentSnapshot> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        StringBuilder contentBuilder = new();

        foreach (AiContextDocumentSnapshot document in documents)
        {
            contentBuilder.AppendLine($"File: {document.FilePath}");
            contentBuilder.AppendLine("```");
            contentBuilder.AppendLine(document.Content);
            contentBuilder.AppendLine("```");
            contentBuilder.AppendLine();
        }

        return new AiChatReference(AiReferenceKind.OpenDocuments, string.Empty, "All open documents")
        {
            Content = contentBuilder.ToString().TrimEnd()
        };
    }

    internal static AiChatReference CreateBuildOutputReference(AiBuildOutputSnapshot buildOutput)
    {
        ArgumentNullException.ThrowIfNull(buildOutput);

        IReadOnlyList<string> lines = buildOutput.Lines.Count <= MaxBuildOutputLines
            ? buildOutput.Lines
            : buildOutput.Lines.Skip(buildOutput.Lines.Count - MaxBuildOutputLines).ToList();

        StringBuilder contentBuilder = new();

        if (!string.IsNullOrWhiteSpace(buildOutput.Summary))
        {
            contentBuilder.AppendLine($"Summary: {buildOutput.Summary}");
            contentBuilder.AppendLine();
        }

        foreach (string line in lines)
        {
            contentBuilder.AppendLine(line);
        }

        if (buildOutput.Lines.Count > MaxBuildOutputLines)
        {
            contentBuilder.AppendLine();
            contentBuilder.AppendLine($"(showing the last {MaxBuildOutputLines} lines of build output)");
        }

        return new AiChatReference(AiReferenceKind.BuildOutput, string.Empty, "Build output")
        {
            Content = contentBuilder.ToString().TrimEnd()
        };
    }

    internal static IReadOnlyList<AiContextClassSnapshot> DiscoverClasses(IReadOnlyList<ProjectItem> projectItems)
    {
        ArgumentNullException.ThrowIfNull(projectItems);

        List<string> filePaths = [];
        CollectCSharpFilePaths(projectItems, filePaths);

        List<AiContextClassSnapshot> classes = [];

        foreach (string filePath in filePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string fileContent = File.ReadAllText(filePath);
                CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(fileContent, path: filePath).GetCompilationUnitRoot();

                foreach (ClassDeclarationSyntax classDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    string displayName = BuildDisplayName(classDeclaration);
                    string sourceText = classDeclaration.NormalizeWhitespace().ToFullString();
                    classes.Add(new AiContextClassSnapshot(displayName, filePath, sourceText));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return classes
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static AiChatReference CreateClassReference(AiContextClassSnapshot classSnapshot)
    {
        ArgumentNullException.ThrowIfNull(classSnapshot);

        return new AiChatReference(AiReferenceKind.Class, classSnapshot.FilePath, classSnapshot.DisplayName)
        {
            Content = classSnapshot.SourceText
        };
    }

    internal static AiChatReference CreateExternalFolderReference(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        string normalizedFolderPath = Path.GetFullPath(folderPath);
        List<string> files = [];
        CollectExternalFiles(normalizedFolderPath, normalizedFolderPath, files);

        StringBuilder contentBuilder = new();
        if (files.Count == 0)
        {
            contentBuilder.AppendLine("(no files found)");
        }
        else
        {
            foreach (string file in files)
            {
                contentBuilder.AppendLine(file);
            }

            if (files.Count == MaxExternalFolderFiles)
            {
                contentBuilder.AppendLine($"... (truncated at {MaxExternalFolderFiles} files)");
            }
        }

        return new AiChatReference(
            AiReferenceKind.ExternalFolder,
            normalizedFolderPath,
            GetFolderDisplayName(normalizedFolderPath))
        {
            Content = contentBuilder.ToString().TrimEnd()
        };
    }

    private static string BuildDisplayName(ClassDeclarationSyntax classDeclaration)
    {
        List<string> nameParts = [classDeclaration.Identifier.ValueText];
        SyntaxNode? currentNode = classDeclaration.Parent;

        while (currentNode is not null)
        {
            if (currentNode is ClassDeclarationSyntax containingClass)
            {
                nameParts.Add(containingClass.Identifier.ValueText);
            }
            else if (currentNode is NamespaceDeclarationSyntax namespaceDeclaration)
            {
                nameParts.Add(namespaceDeclaration.Name.ToString());
            }
            else if (currentNode is FileScopedNamespaceDeclarationSyntax fileScopedNamespaceDeclaration)
            {
                nameParts.Add(fileScopedNamespaceDeclaration.Name.ToString());
            }

            currentNode = currentNode.Parent;
        }

        nameParts.Reverse();
        return string.Join(".", nameParts);
    }

    private static void CollectCSharpFilePaths(IReadOnlyList<ProjectItem> items, List<string> filePaths)
    {
        foreach (ProjectItem item in items)
        {
            if (item.ItemType == ProjectItemType.File &&
                item.FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(item.FullPath))
            {
                filePaths.Add(item.FullPath);
            }

            if (item.Children.Count > 0)
            {
                CollectCSharpFilePaths(item.Children, filePaths);
            }
        }
    }

    private static void CollectExternalFiles(string rootDirectory, string currentDirectory, List<string> files)
    {
        if (!Directory.Exists(currentDirectory) || files.Count >= MaxExternalFolderFiles)
        {
            return;
        }

        try
        {
            foreach (string filePath in Directory.EnumerateFiles(currentDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (files.Count >= MaxExternalFolderFiles)
                {
                    return;
                }

                files.Add(Path.GetRelativePath(rootDirectory, filePath));
            }

            foreach (string directoryPath in Directory.EnumerateDirectories(currentDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (files.Count >= MaxExternalFolderFiles)
                {
                    return;
                }

                string directoryName = Path.GetFileName(directoryPath);
                if (SkippedDirectories.Contains(directoryName))
                {
                    continue;
                }

                CollectExternalFiles(rootDirectory, directoryPath, files);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string GetFolderDisplayName(string folderPath)
    {
        string trimmedPath = Path.TrimEndingDirectorySeparator(folderPath);
        string displayName = Path.GetFileName(trimmedPath);

        return string.IsNullOrWhiteSpace(displayName)
            ? trimmedPath
            : displayName;
    }
}
