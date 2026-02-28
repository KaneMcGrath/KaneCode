using KaneCode.Models;
using System.IO;
using System.Text.Json;
using System.Xml.Linq;

namespace KaneCode.Services;

/// <summary>
/// Loads, saves, and expands user-editable file templates.
/// </summary>
internal sealed class TemplateService
{
    private const string NamespacePlaceholder = "{NAMESPACE}";
    private const string NamePlaceholder = "{NAME}";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _templateFilePath;

    public TemplateService()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _templateFilePath = Path.Combine(appDataPath, "KaneCode", "templates.json");
    }

    internal IReadOnlyList<FileTemplate> GetTemplates()
    {
        if (!File.Exists(_templateFilePath))
        {
            var defaults = GetDefaultTemplates();
            SaveTemplates(defaults);
            return defaults;
        }

        var json = File.ReadAllText(_templateFilePath);
        var templates = JsonSerializer.Deserialize<List<FileTemplate>>(json, SerializerOptions);
        if (templates is null || templates.Count == 0)
        {
            var defaults = GetDefaultTemplates();
            SaveTemplates(defaults);
            return defaults;
        }

        return templates;
    }

    internal void SaveTemplates(IReadOnlyList<FileTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);

        var directory = Path.GetDirectoryName(_templateFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(templates, SerializerOptions);
        File.WriteAllText(_templateFilePath, json);
    }

    internal string GenerateFromTemplate(string templateName, string fileName, string targetFolder, string projectRootPath)
    {
        if (string.IsNullOrWhiteSpace(templateName))
        {
            throw new ArgumentException("Template name is required.", nameof(templateName));
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("File name is required.", nameof(fileName));
        }

        if (string.IsNullOrWhiteSpace(targetFolder))
        {
            throw new ArgumentException("Target folder is required.", nameof(targetFolder));
        }

        if (string.IsNullOrWhiteSpace(projectRootPath))
        {
            throw new ArgumentException("Project root path is required.", nameof(projectRootPath));
        }

        var template = GetTemplates().FirstOrDefault(t =>
            string.Equals(t.Name, templateName, StringComparison.OrdinalIgnoreCase));

        if (template is null)
        {
            throw new InvalidOperationException($"Template '{templateName}' was not found.");
        }

        var resolvedName = SanitizeIdentifier(Path.GetFileNameWithoutExtension(fileName));
        var rootNamespace = ResolveRootNamespace(projectRootPath);
        var resolvedNamespace = ResolveNamespace(targetFolder, projectRootPath, rootNamespace);

        return template.Body
            .Replace(NamespacePlaceholder, resolvedNamespace, StringComparison.Ordinal)
            .Replace(NamePlaceholder, resolvedName, StringComparison.Ordinal);
    }

    private static List<FileTemplate> GetDefaultTemplates()
    {
        return
        [
            new FileTemplate
            {
                Name = "Class",
                Body = """
namespace {NAMESPACE};

internal sealed class {NAME}
{
}
"""
            },
            new FileTemplate
            {
                Name = "Interface",
                Body = """
namespace {NAMESPACE};

internal interface I{NAME}
{
}
"""
            },
            new FileTemplate
            {
                Name = "Enum",
                Body = """
namespace {NAMESPACE};

internal enum {NAME}
{
}
"""
            },
            new FileTemplate
            {
                Name = "Record",
                Body = """
namespace {NAMESPACE};

internal sealed record {NAME};
"""
            },
            new FileTemplate
            {
                Name = "Struct",
                Body = """
namespace {NAMESPACE};

internal struct {NAME}
{
}
"""
            },
            new FileTemplate
            {
                Name = "Empty",
                Body = """
namespace {NAMESPACE};

"""
            }
        ];
    }

    private static string ResolveNamespace(string targetFolder, string projectRootPath, string rootNamespace)
    {
        var relativePath = Path.GetRelativePath(projectRootPath, targetFolder);
        if (string.IsNullOrWhiteSpace(relativePath)
            || string.Equals(relativePath, ".", StringComparison.Ordinal)
            || relativePath.StartsWith("..", StringComparison.Ordinal))
        {
            return rootNamespace;
        }

        var segments = relativePath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeIdentifier)
            .Where(s => !string.IsNullOrWhiteSpace(s));

        var suffix = string.Join('.', segments);
        return string.IsNullOrWhiteSpace(suffix)
            ? rootNamespace
            : $"{rootNamespace}.{suffix}";
    }

    private static string ResolveRootNamespace(string projectRootPath)
    {
        var projectFile = Directory
            .EnumerateFiles(projectRootPath, "*.csproj", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(projectFile))
        {
            var document = XDocument.Load(projectFile);
            var rootNamespace = document
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "RootNamespace")?
                .Value;

            if (!string.IsNullOrWhiteSpace(rootNamespace))
            {
                return rootNamespace.Trim();
            }

            var assemblyName = document
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?
                .Value;

            if (!string.IsNullOrWhiteSpace(assemblyName))
            {
                return SanitizeIdentifier(assemblyName);
            }

            return SanitizeIdentifier(Path.GetFileNameWithoutExtension(projectFile));
        }

        return SanitizeIdentifier(Path.GetFileName(projectRootPath));
    }

    private static string SanitizeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Generated";
        }

        var chars = value
            .Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_')
            .ToArray();

        var normalized = new string(chars).Trim('_');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Generated";
        }

        if (!char.IsLetter(normalized[0]) && normalized[0] != '_')
        {
            normalized = $"_{normalized}";
        }

        return normalized;
    }

    }
