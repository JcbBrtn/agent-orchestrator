using System.Text.RegularExpressions;
using AgentOrchestrator.Core.Models;

namespace AgentOrchestrator.Core.Engine;

public record RouteChoice(string ArrowName, string? Message);

public static class ArrowRouting
{
    public static bool ShouldFire(ArrowDefinition arrow, string output, IReadOnlyCollection<RouteChoice> choices, out string? choiceMessage)
    {
        choiceMessage = null;
        var condition = arrow.Condition ?? new ArrowCondition();
        bool match;
        switch (condition.Type)
        {
            case ConditionType.Always:
                match = true;
                break;
            case ConditionType.Keyword:
                match = !string.IsNullOrEmpty(condition.Value)
                    && output.Contains(condition.Value, StringComparison.OrdinalIgnoreCase);
                break;
            case ConditionType.Regex:
                match = !string.IsNullOrEmpty(condition.Value)
                    && Regex.IsMatch(output, condition.Value, RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromSeconds(2));
                break;
            case ConditionType.AgentChoice:
                var choice = choices.LastOrDefault(c => string.Equals(c.ArrowName, arrow.Name, StringComparison.OrdinalIgnoreCase));
                match = choice is not null;
                choiceMessage = choice?.Message;
                break;
            default:
                match = false;
                break;
        }
        return condition.Negate ? !match : match;
    }

    public static string Render(string template, string output, string input, string task, string source, string? message) =>
        (template ?? "")
            .Replace("{{output}}", output, StringComparison.OrdinalIgnoreCase)
            .Replace("{{input}}", input, StringComparison.OrdinalIgnoreCase)
            .Replace("{{task}}", task, StringComparison.OrdinalIgnoreCase)
            .Replace("{{source}}", source, StringComparison.OrdinalIgnoreCase)
            .Replace("{{message}}", message ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="to"/> is reachable from <paramref name="from"/> following arrows (from == to counts as reachable).</summary>
    public static bool CanReach(GraphDefinition graph, string from, string to)
    {
        if (from == to) return true;
        var seen = new HashSet<string> { from };
        var stack = new Stack<string>();
        stack.Push(from);
        while (stack.Count > 0)
        {
            foreach (var arrow in graph.Outgoing(stack.Pop()))
            {
                if (arrow.TargetAgentId == to) return true;
                if (seen.Add(arrow.TargetAgentId)) stack.Push(arrow.TargetAgentId);
            }
        }
        return false;
    }
}
