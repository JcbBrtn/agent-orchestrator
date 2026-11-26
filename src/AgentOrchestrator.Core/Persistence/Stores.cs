using System.Text.Encodings.Web;
using System.Text.Json;
using AgentOrchestrator.Core.Models;

namespace AgentOrchestrator.Core.Persistence;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static T? Load<T>(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : default;

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Resolves the on-disk layout under a single data root (default %AppData%\AgentOrchestrator).</summary>
public class DataPaths(string? root = null)
{
    public string Root { get; } = root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentOrchestrator");

    public string GraphsDir => Ensure(Path.Combine(Root, "graphs"));
    public string SkillsDir => Ensure(Path.Combine(Root, "skills"));
    public string RunsDir => Ensure(Path.Combine(Root, "runs"));
    public string SessionsDir => Ensure(Path.Combine(Root, "sessions"));
    public string SettingsFile => Path.Combine(Ensure(Root), "settings.json");
    public string SecretsFile => Path.Combine(Ensure(Root), "secrets.json");

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public class GraphStore(DataPaths paths)
{
    public GraphDefinition Load(string path) =>
        Json.Load<GraphDefinition>(path) ?? throw new InvalidDataException($"Could not read graph '{path}'.");

    public void Save(string path, GraphDefinition graph) => Json.Save(path, graph);

    public IEnumerable<string> List() => Directory.EnumerateFiles(paths.GraphsDir, "*.json");
}

public class SessionStore(DataPaths paths)
{
    /// <summary>All sessions, most recently used first. Unreadable files are skipped.</summary>
    public IReadOnlyList<SessionDefinition> List()
    {
        var sessions = new List<SessionDefinition>();
        foreach (var file in Directory.EnumerateFiles(paths.SessionsDir, "*.json"))
        {
            try
            {
                if (Json.Load<SessionDefinition>(file) is { } s)
                    sessions.Add(s);
            }
            catch (JsonException) { }
        }
        return sessions.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    public SessionDefinition? TryGet(string id)
    {
        var file = FileFor(id);
        return File.Exists(file) ? Json.Load<SessionDefinition>(file) : null;
    }

    public void Save(SessionDefinition session) => Json.Save(FileFor(session.Id), session);

    public void Delete(string id)
    {
        var file = FileFor(id);
        if (File.Exists(file))
            File.Delete(file);
    }

    private string FileFor(string id) => Path.Combine(paths.SessionsDir, Path.GetFileName(id) + ".json");
}

public class SettingsStore(DataPaths paths)
{
    public AppSettings Load()
    {
        var settings = Json.Load<AppSettings>(paths.SettingsFile);
        if (settings is null)
        {
            settings = AppSettings.CreateDefault();
            Save(settings);
        }
        return settings;
    }

    public void Save(AppSettings settings) => Json.Save(paths.SettingsFile, settings);
}

/// <summary>
/// Skills are markdown files with optional front matter:
/// ---
/// name: Code reviewer
/// description: How to review C# code
/// ---
/// body...
/// </summary>
public class SkillStore(DataPaths paths)
{
    public IReadOnlyList<SkillDefinition> LoadAll() =>
        Directory.EnumerateFiles(paths.SkillsDir, "*.md")
            .Select(Load)
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public SkillDefinition? TryGet(string id)
    {
        var file = Path.Combine(paths.SkillsDir, id + ".md");
        return File.Exists(file) ? Load(file) : null;
    }

    public void Save(SkillDefinition skill)
    {
        if (string.IsNullOrWhiteSpace(skill.Id))
            skill.Id = MakeId(skill.Name);
        var text = $"---\nname: {skill.Name}\ndescription: {skill.Description}\n---\n{skill.Content}";
        File.WriteAllText(Path.Combine(paths.SkillsDir, skill.Id + ".md"), text);
    }

    public void Delete(string id)
    {
        var file = Path.Combine(paths.SkillsDir, id + ".md");
        if (File.Exists(file))
            File.Delete(file);
    }

    public static SkillDefinition Parse(string id, string text)
    {
        var skill = new SkillDefinition { Id = id, Name = id, Content = text };
        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n"))
            return skill;

        var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
            return skill;

        foreach (var line in normalized[4..end].Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim().ToLowerInvariant();
            var value = line[(idx + 1)..].Trim();
            if (key == "name") skill.Name = value;
            else if (key == "description") skill.Description = value;
        }

        var bodyStart = normalized.IndexOf('\n', end + 1);
        skill.Content = bodyStart < 0 ? "" : normalized[(bodyStart + 1)..];
        return skill;
    }

    private static SkillDefinition Load(string file) =>
        Parse(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file));

    private static string MakeId(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var id = new string(chars).Trim('-');
        return string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id;
    }
}
