using AgentOrchestrator.Core.Models;
using AgentOrchestrator.Core.Persistence;

namespace AgentOrchestrator.Core.Tests;

public class GraphMergerTests
{
    private static GraphDefinition SourceGraph()
    {
        var a = new AgentDefinition { Id = "a", Name = "Planner", X = 100, Y = 50 };
        var b = new AgentDefinition { Id = "b", Name = "Coder", X = 300, Y = 80 };
        return new GraphDefinition
        {
            Agents = [a, b],
            Arrows = [new ArrowDefinition { SourceAgentId = "a", TargetAgentId = "b", Name = "to_coder" }],
        };
    }

    [Fact]
    public void Merge_clones_with_prefixed_names_offset_and_remapped_arrows()
    {
        var target = new GraphDefinition();
        var ids = GraphMerger.Merge(target, SourceGraph(), "a", anchorX: 1000, anchorY: 500, index: 2);

        Assert.Equal(2, ids.Count);
        var planner = target.Agents[0];
        var coder = target.Agents[1];
        Assert.Equal("Imported_Planner_2", planner.Name);
        Assert.Equal("Imported_Coder_2", coder.Name);
        Assert.Equal(1000, planner.X);
        Assert.Equal(500, planner.Y);
        Assert.Equal(1200, coder.X);
        Assert.NotEqual("a", planner.Id);

        var arrow = Assert.Single(target.Arrows);
        Assert.Equal(planner.Id, arrow.SourceAgentId);
        Assert.Equal(coder.Id, arrow.TargetAgentId);
        Assert.Equal("to_coder", arrow.Name);
    }

    [Fact]
    public void Merge_marks_only_the_chosen_primary_hook_and_survives_json_round_trip()
    {
        var target = new GraphDefinition();
        GraphMerger.Merge(target, SourceGraph(), "a", anchorX: 0, anchorY: 0, index: 1);

        var path = Path.Combine(Path.GetTempPath(), $"ao-merge-{Guid.NewGuid():N}.json");
        try
        {
            Json.Save(path, target);
            var loaded = Json.Load<GraphDefinition>(path)!;
            Assert.True(loaded.Agents[0].IsPrimaryHook);
            Assert.False(loaded.Agents[1].IsPrimaryHook);
            Assert.Equal("Imported_Planner_1", loaded.Agents[0].Name);
            Assert.Equal(loaded.Agents[0].Id, loaded.Arrows[0].SourceAgentId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FindEntryPointIds_returns_agents_no_arrow_targets()
    {
        Assert.Equal(["a"], GraphMerger.FindEntryPointIds(SourceGraph()));
    }
}
