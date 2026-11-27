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
    public void EditFile_matches_LF_text_against_CRLF_file_and_keeps_CRLF()
    {
        var dir = Directory.CreateTempSubdirectory("ao-edit").FullName;
        try
        {
            var box = new AgentToolbox(dir);
            var path = Path.Combine(dir, "todo.md");
            File.WriteAllText(path, "1. one\r\n2. two\r\n3. three\r\n");
            Assert.StartsWith("Edited", box.EditFile("todo.md", "1. one\n2. two", "1. ONE\n2. TWO"));
            Assert.Equal("1. ONE\r\n2. TWO\r\n3. three\r\n", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EditFile_preserves_UTF8_BOM()
    {
        var dir = Directory.CreateTempSubdirectory("ao-edit").FullName;
        try
        {
            var box = new AgentToolbox(dir);
            var path = Path.Combine(dir, "bom.txt");
            File.WriteAllBytes(path, [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. "hello world"u8]);
            Assert.StartsWith("Edited", box.EditFile("bom.txt", "hello", "bye"));
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            Assert.Equal("bye world", System.Text.Encoding.UTF8.GetString(bytes[3..]));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EditFile_not_found_reports_closest_line()
    {
        var dir = Directory.CreateTempSubdirectory("ao-edit").FullName;
        try
        {
            var box = new AgentToolbox(dir);
            File.WriteAllText(Path.Combine(dir, "f.txt"), "alpha\r\nbeta gamma\r\n");
            var result = box.EditFile("f.txt", "beta delta", "x");
            Assert.StartsWith("Error: old_str not found", result);
            Assert.Contains("Closest line 2", result);
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
    public void Rerun_deletes_only_the_session_runs()
    {
        var dir = Directory.CreateTempSubdirectory("ao-rerun").FullName;
        try
        {
            var logger = new RunLogger(dir);
            var graph = new GraphDefinition { Name = "g", Agents = [new AgentDefinition { Id = "a", Name = "A" }] };
            foreach (var runId in new[] { "run1", "other" })
            {
                logger.BeginRun(runId, graph, new RunRequest("a", "task", dir, null));
                logger.EndRun(runId, new RunResult(runId, RunStatus.Completed, [], null, 1, null));
            }
            var session = new SessionDefinition { RunIds = ["run1", "missing"] };

            SessionRerun.DeleteRuns(logger, session);

            Assert.Empty(session.RunIds);
            Assert.False(Directory.Exists(Path.Combine(dir, "run1")));
            Assert.NotNull(logger.LoadSummary("other"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Rerun_uses_first_run_as_starting_prompt()
    {
        var dir = Directory.CreateTempSubdirectory("ao-first").FullName;
        try
        {
            var logger = new RunLogger(dir);
            var graph = new GraphDefinition { Name = "g", Agents = [new AgentDefinition { Id = "a", Name = "Planner" }] };
            var request = new RunRequest("a", "build the thing", dir, null);
            logger.BeginRun("run1", graph, request);
            logger.EndRun("run1", new RunResult("run1", RunStatus.Completed, [], null, 1, null));
            var session = new SessionDefinition { RunIds = ["run1", "missing"] };

            var first = SessionRerun.FirstRun(logger, session);
            Assert.Equal("build the thing", first?.Task);
            Assert.Equal("Planner", first?.EntryAgentName);
            Assert.Null(SessionRerun.FirstRun(logger, new SessionDefinition()));
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