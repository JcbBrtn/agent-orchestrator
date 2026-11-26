using System.Reflection;
using System.Text.Json;
using AgentOrchestrator.Core.Models;

namespace AgentOrchestrator.Core.Persistence;

/// <summary>The orchestrations that ship with the app (the files in the repo's samples folder, embedded as resources).</summary>
public static class BuiltInGraphs
{
    private const string Prefix = "BuiltInGraphs/";

    public static IReadOnlyList<(string FileName, string Json)> ReadAll()
    {
        var assembly = typeof(BuiltInGraphs).Assembly;
        var result = new List<(string, string)>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            result.Add((name[Prefix.Length..], reader.ReadToEnd()));
        }
        return result;
    }

    /// <summary>Copies built-in graphs that were never seeded before into the graphs folder; returns true if settings changed.</summary>
    public static bool Seed(DataPaths paths, AppSettings settings)
    {
        var changed = false;
        foreach (var (fileName, json) in ReadAll())
        {
            GraphDefinition? graph;
            try { graph = JsonSerializer.Deserialize<GraphDefinition>(json, Json.Options); }
            catch (JsonException) { continue; }
            if (graph is null || settings.SeededGraphs.Contains(graph.Id)) continue;

            var target = Path.Combine(paths.GraphsDir, fileName);
            if (!File.Exists(target))
                File.WriteAllText(target, json);
            settings.SeededGraphs.Add(graph.Id);
            changed = true;
        }
        return changed;
    }
}
