using AgentOrchestrator.Core.Engine;
using AgentOrchestrator.Core.Models;
using AgentOrchestrator.Core.Persistence;
using AgentOrchestrator.Core.Tools;

namespace AgentOrchestrator.Core.Tests;

public class ToolsAndStoresTests
{
    [Theory]
    [InlineData(ToolNames.RunPowerShell, null, true)]
    [InlineData(ToolNames.CreateFile, "inside.txt", false)]
    [InlineData(ToolNames.EditFile, @"C:\elsewhere\x.txt", true)]
    [InlineData(ToolNames.ReadFile, @"C:\elsewhere\x.txt", false)]
    public void IsRisky_classifies_tools(string tool, string? path, bool expected)
    {
        var args = new Dictionary<string, object?> { ["path"] = path };
        Assert.Equal(expected, AgentToolbox.IsRisky(tool, args, @"C:\work"));
    }

    [Fact]
    public void Web_get_is_not_risky_but_post_is()
    {
        Assert.False(AgentToolbox.IsRisky(ToolNames.WebRequest, new Dictionary<string, object?> { ["method"] = "GET" }, @"C:\work"));
        Assert.True(AgentToolbox.IsRisky(ToolNames.WebRequest, new Dictionary<string, object?> { ["method"] = "POST" }, @"C:\work"));
    }

    [Fact]
    public void EditFile_requires_unique_match()
    {
        var dir = Directory.CreateTempSubdirectory("ao-edit").FullName;
        try
        {
            var box = new AgentToolbox(dir);
            File.WriteAllText(Path.Combine(dir, "f.txt"), "a b a");
            Assert.StartsWith("Error", box.EditFile("f.txt", "a", "z"));
            Assert.StartsWith("Edited", box.EditFile("f.txt", "b", "c"));
            Assert.Equal("a c a", File.ReadAllText(Path.Combine(dir, "f.txt")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Skill_front_matter_is_parsed()
    {
        var skill = SkillStore.Parse("rev", "---\nname: Reviewer\ndescription: Reviews code\n---\nBe strict.\n");
        Assert.Equal("Reviewer", skill.Name);
        Assert.Equal("Reviews code", skill.Description);
        Assert.Equal("Be strict.\n", skill.Content);
    }

    [Fact]
    public void Graph_round_trips_through_json()
    {
        var dir = Directory.CreateTempSubdirectory("ao-graph").FullName;
        try
        {
            var graph = new GraphDefinition { Name = "g", Agents = [new AgentDefinition { Id = "a", ApprovalMode = ApprovalMode.AlwaysAsk }],
                Arrows = [new ArrowDefinition { Mode = ArrowMode.Handoff, Condition = new() { Type = ConditionType.Regex, Value = "x" } }] };
            var store = new GraphStore(new DataPaths(dir));
            var path = Path.Combine(dir, "g.json");
            store.Save(path, graph);
            var loaded = store.Load(path);
            Assert.Equal(ApprovalMode.AlwaysAsk, loaded.Agents[0].ApprovalMode);
            Assert.Equal(ArrowMode.Handoff, loaded.Arrows[0].Mode);
            Assert.Equal(ConditionType.Regex, loaded.Arrows[0].Condition.Type);
            Assert.Contains("\"AlwaysAsk\"", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Sessions_round_trip_and_build_history()
    {
        var dir = Directory.CreateTempSubdirectory("ao-sessions").FullName;
        try
        {
            var paths = new DataPaths(dir);
            var store = new SessionStore(paths);
            var logger = new RunLogger(paths.RunsDir);
            var graph = new GraphDefinition { Agents = [new AgentDefinition { Id = "a", Name = "Coder" }] };
            logger.BeginRun("r1", graph, new RunRequest("a", "build parser", @"C:\work"));
            logger.EndRun("r1", new RunResult("r1", RunStatus.Completed, [new RunOutput("a", "Coder", "parser done")], null, 1, null));

            var older = new SessionDefinition { Name = "old", UpdatedAt = DateTimeOffset.Now.AddDays(-1) };
            var newer = new SessionDefinition { Name = "new", WorkingDirectory = @"C:\work", GraphPath = @"C:\g.json", RunIds = ["r1", "missing"] };
            store.Save(older);
            store.Save(newer);

            Assert.Equal(["new", "old"], store.List().Select(s => s.Name));
            var loaded = store.TryGet(newer.Id)!;
            Assert.Equal(@"C:\g.json", loaded.GraphPath);

            var history = SessionHistory.Build(logger, loaded)!;
            Assert.Contains("build parser", history);
            Assert.Contains("Result from Coder: parser done", history);

            store.Delete(older.Id);
            Assert.Single(store.List());
        }
        finally { Directory.Delete(dir, true); }
    }
}