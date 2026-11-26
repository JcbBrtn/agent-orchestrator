using System.Text;
using AgentOrchestrator.Core.Models;

namespace AgentOrchestrator.Core.Engine;

/// <summary>Builds the "Earlier in this session" context from a session's previous runs.</summary>
public static class SessionHistory
{
    public static string? Build(RunLogger logger, SessionDefinition session, int maxRuns = 5, int maxOutputChars = 1500)
    {
        var summaries = session.RunIds
            .TakeLast(maxRuns)
            .Select(logger.LoadSummary)
            .Where(s => s is not null)
            .ToList();
        if (summaries.Count == 0)
            return null;

        var sb = new StringBuilder();
        foreach (var run in summaries)
        {
            sb.AppendLine($"## Task ({run!.StartedAt:g}, {run.Status?.ToString() ?? "unfinished"}): {run.Task}");
            foreach (var output in run.Outputs ?? [])
            {
                var text = output.Output.Trim();
                if (text.Length > maxOutputChars)
                    text = text[..maxOutputChars] + " …(truncated)";
                sb.AppendLine($"Result from {output.AgentName}: {text}");
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
