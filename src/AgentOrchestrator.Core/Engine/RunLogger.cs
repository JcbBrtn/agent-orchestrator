using System.Collections.Concurrent;
using System.Text.Json;
using AgentOrchestrator.Core.Models;
using AgentOrchestrator.Core.Persistence;

namespace AgentOrchestrator.Core.Engine;

public record RunSummary(string RunId, string GraphName, string EntryAgentName, string Task, string WorkingDirectory,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, RunStatus? Status, string? Error, int Steps, IReadOnlyList<RunOutput>? Outputs = null);

/// <summary>
/// Writes each run to {runsDir}\{runId}\: graph.json (snapshot), run.json (summary), events.jsonl (events, excluding streamed tokens).
/// </summary>
public class RunLogger(string runsDir)
{
    private readonly ConcurrentDictionary<string, (string Dir, RunSummary Summary, object Gate)> _runs = new();

    public string RunsDirectory => runsDir;

    public string BeginRun(string runId, GraphDefinition graph, RunRequest request)
    {
        var dir = Path.Combine(runsDir, runId);
        Directory.CreateDirectory(dir);
        Json.Save(Path.Combine(dir, "graph.json"), graph);
        var summary = new RunSummary(runId, graph.Name, graph.FindAgent(request.EntryAgentId)?.Name ?? "?", request.Task,
            request.WorkingDirectory, DateTimeOffset.Now, null, null, null, 0);
        Json.Save(Path.Combine(dir, "run.json"), summary);
        _runs[runId] = (dir, summary, new object());
        return dir;
    }

    public void Write(string runId, RunEvent e)
    {
        if (e.Kind == RunEventKind.Token || !_runs.TryGetValue(runId, out var run))
            return;
        var line = JsonSerializer.Serialize(e, Json.Compact) + Environment.NewLine;
        lock (run.Gate)
            File.AppendAllText(Path.Combine(run.Dir, "events.jsonl"), line);
    }

    public void EndRun(string runId, RunResult result)
    {
        if (!_runs.TryRemove(runId, out var run))
            return;
        Json.Save(Path.Combine(run.Dir, "run.json"),
            run.Summary with { EndedAt = DateTimeOffset.Now, Status = result.Status, Error = result.Error, Steps = result.Steps, Outputs = result.FinalOutputs });
    }

    public void DeleteRun(string runId)
    {
        var dir = Path.Combine(runsDir, Path.GetFileName(runId));
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    public IReadOnlyList<RunSummary> ListRuns() =>
        Directory.EnumerateDirectories(runsDir)
            .Select(d => { try { return Json.Load<RunSummary>(Path.Combine(d, "run.json")); } catch { return null; } })
            .Where(s => s is not null)
            .Select(s => s!)
            .OrderByDescending(s => s.StartedAt)
            .ToList();

    public RunSummary? LoadSummary(string runId)
    {
        try { return Json.Load<RunSummary>(Path.Combine(runsDir, Path.GetFileName(runId), "run.json")); }
        catch (JsonException) { return null; }
    }

    public IReadOnlyList<RunEvent> LoadEvents(string runId)
    {
        var file = Path.Combine(runsDir, runId, "events.jsonl");
        if (!File.Exists(file)) return [];
        return File.ReadLines(file)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<RunEvent>(l, Json.Options)!)
            .ToList();
    }
}
