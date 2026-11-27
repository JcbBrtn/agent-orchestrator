namespace AgentOrchestrator.Core.Models;

/// <summary>Flattens an orchestration into another graph: clones its agents and arrows with fresh ids, prefixed names and offset coordinates.</summary>
public static class GraphMerger
{
    /// <summary>Clones the source's agents and arrows into target. Returns the ids of the imported agents, in source order.</summary>
    public static List<string> Merge(GraphDefinition target, GraphDefinition source, string primaryHookAgentId, double anchorX, double anchorY, int index)
    {
        var idMap = new Dictionary<string, string>();
        var minX = source.Agents.Count == 0 ? 0 : source.Agents.Min(a => a.X);
        var minY = source.Agents.Count == 0 ? 0 : source.Agents.Min(a => a.Y);

        foreach (var agent in source.Agents)
        {
            var clone = Clone(agent);
            clone.Id = Guid.NewGuid().ToString("N");
            clone.Name = $"Imported_{agent.Name}_{index}";
            clone.X = anchorX + (agent.X - minX);
            clone.Y = anchorY + (agent.Y - minY);
            clone.IsPrimaryHook = agent.Id == primaryHookAgentId;
            idMap[agent.Id] = clone.Id;
            target.Agents.Add(clone);
        }

        foreach (var arrow in source.Arrows)
        {
            if (!idMap.TryGetValue(arrow.SourceAgentId, out var sourceId) || !idMap.TryGetValue(arrow.TargetAgentId, out var targetId))
                continue;
            var clone = new ArrowDefinition
            {
                SourceAgentId = sourceId,
                TargetAgentId = targetId,
                Name = arrow.Name,
                Description = arrow.Description,
                Mode = arrow.Mode,
                Prompt = arrow.Prompt,
                Condition = new ArrowCondition { Type = arrow.Condition.Type, Value = arrow.Condition.Value, Negate = arrow.Condition.Negate },
                MaxTraversals = arrow.MaxTraversals,
            };
            target.Arrows.Add(clone);
        }

        return source.Agents.Select(a => idMap[a.Id]).ToList();
    }

    /// <summary>Entry points are agents that no arrow targets.</summary>
    public static List<string> FindEntryPointIds(GraphDefinition graph)
    {
        var targeted = graph.Arrows.Select(a => a.TargetAgentId).ToHashSet();
        return graph.Agents.Where(a => !targeted.Contains(a.Id)).Select(a => a.Id).ToList();
    }

    private static AgentDefinition Clone(AgentDefinition agent) => new()
    {
        Name = agent.Name,
        ProviderId = agent.ProviderId,
        ModelId = agent.ModelId,
        SystemPrompt = agent.SystemPrompt,
        Temperature = agent.Temperature,
        TopP = agent.TopP,
        TopK = agent.TopK,
        EnabledTools = [.. agent.EnabledTools],
        SkillIds = [.. agent.SkillIds],
        ApprovalMode = agent.ApprovalMode,
        JoinMode = agent.JoinMode,
        MaxToolIterations = agent.MaxToolIterations,
        KeepLoopMemory = agent.KeepLoopMemory,
        MemoryMaxMessages = agent.MemoryMaxMessages,
        X = agent.X,
        Y = agent.Y,
    };
}
