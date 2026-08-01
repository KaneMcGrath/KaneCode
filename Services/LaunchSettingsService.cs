using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KaneCode.Services;

internal sealed class LaunchProfile
{
    public string Name { get; set; } = string.Empty;
    public string CommandName { get; set; } = "Project";
    public string CommandLineArgs { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public bool LaunchBrowser { get; set; }
    public string LaunchUrl { get; set; } = string.Empty;
    public Dictionary<string, string> EnvironmentVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal static class LaunchSettingsService
{
    private static readonly JsonSerializerOptions s_options = new() { WriteIndented = true };

    public static string GetPath(string projectPath)
    {
        string directory = Directory.Exists(projectPath)
            ? projectPath
            : Path.GetDirectoryName(projectPath) ?? projectPath;
        return Path.Combine(directory, "Properties", "launchSettings.json");
    }

    public static List<LaunchProfile> Load(string projectPath)
    {
        string path = GetPath(projectPath);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            JsonNode? root = JsonNode.Parse(File.ReadAllText(path));
            JsonObject? profiles = root?["profiles"] as JsonObject;
            if (profiles is null)
            {
                return [];
            }

            List<LaunchProfile> result = [];
            foreach (KeyValuePair<string, JsonNode?> entry in profiles)
            {
                JsonObject? json = entry.Value as JsonObject;
                if (json is null)
                {
                    continue;
                }

                LaunchProfile profile = new()
                {
                    Name = entry.Key,
                    CommandName = GetString(json, "commandName", "Project"),
                    CommandLineArgs = GetString(json, "commandLineArgs"),
                    WorkingDirectory = GetString(json, "workingDirectory"),
                    ExecutablePath = GetString(json, "executablePath"),
                    LaunchBrowser = GetBool(json, "launchBrowser"),
                    LaunchUrl = GetString(json, "launchUrl")
                };

                if (json["environmentVariables"] is JsonObject variables)
                {
                    foreach (KeyValuePair<string, JsonNode?> variable in variables)
                    {
                        if (variable.Value is JsonValue value && value.TryGetValue<string>(out string? text))
                        {
                            profile.EnvironmentVariables[variable.Key] = text ?? string.Empty;
                        }
                    }
                }

                result.Add(profile);
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    public static void Save(string projectPath, IReadOnlyList<LaunchProfile> profiles)
    {
        string path = GetPath(projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        JsonObject root = [];
        if (File.Exists(path))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
            }
            catch (JsonException)
            {
                root = [];
            }
        }

        JsonObject profileObject = [];
        foreach (LaunchProfile profile in profiles)
        {
            JsonObject json = root["profiles"]?[profile.Name]?.DeepClone() as JsonObject ?? [];
            json["commandName"] = profile.CommandName;
            SetOrRemove(json, "commandLineArgs", profile.CommandLineArgs);
            SetOrRemove(json, "workingDirectory", profile.WorkingDirectory);
            SetOrRemove(json, "executablePath", profile.ExecutablePath);
            SetOrRemove(json, "launchUrl", profile.LaunchUrl);
            if (profile.LaunchBrowser) json["launchBrowser"] = true; else json.Remove("launchBrowser");

            if (profile.EnvironmentVariables.Count > 0)
            {
                JsonObject variables = [];
                foreach (KeyValuePair<string, string> variable in profile.EnvironmentVariables)
                {
                    variables[variable.Key] = variable.Value;
                }
                json["environmentVariables"] = variables;
            }
            else
            {
                json.Remove("environmentVariables");
            }

            profileObject[profile.Name] = json;
        }

        root["profiles"] = profileObject;
        File.WriteAllText(path, root.ToJsonString(s_options) + Environment.NewLine);
    }

    private static string GetString(JsonObject json, string name, string fallback = "") =>
        json[name]?.GetValue<string>() ?? fallback;

    private static bool GetBool(JsonObject json, string name) =>
        json[name]?.GetValue<bool>() ?? false;

    private static void SetOrRemove(JsonObject json, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) json.Remove(name); else json[name] = value;
    }
}
