using System.Text.Json;
using System.Text.RegularExpressions;
using AgentOrchestrator.Core.Engine;
using AgentOrchestrator.Core.Models;
using AgentOrchestrator.Core.Persistence;
using AgentOrchestrator.Core.Tools;
using Microsoft.Extensions.AI;

namespace AgentOrchestrator.Core.Tests;

public class BuiltInGraphsTests
{
    private static GraphDefinition Load(string json) => JsonSerializer.Deserialize<GraphDefinition>(json, Json.Options)!;

    public static IEnumerable<object[]> Graphs() => BuiltInGraphs.ReadAll().Select(g => new object[] { g.FileName });

    private static GraphDefinition Get(string fileName) => Load(BuiltInGraphs.ReadAll().Single(g => g.FileName == fileName).Json);

    [Fact]
    public void Eight_graphs_ship()
    {
        Assert.Equal(8, BuiltInGraphs.ReadAll().Count);
    }

    [Theory]
    [MemberData(nameof(Graphs))]
    public void Graph_is_consistent_and_cites_papers(string fileName)
    {
        var g = Get(fileName);
        var ids = g.Agents.Select(a => a.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(g.Arrows.Count, g.Arrows.Select(a => a.Id).Distinct().Count());

        Assert.False(string.IsNullOrWhiteSpace(g.Description));
        Assert.NotEmpty(g.References);
        Assert.All(g.References, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Title));
            Assert.StartsWith("https://arxiv.org/abs/", r.Url);
        });

        Assert.All(g.Agents, a =>
        {
            Assert.Equal("copilot", a.ProviderId);
            Assert.All(a.EnabledTools, t => Assert.Contains(t, ToolNames.All));
            Assert.All(a.SkillIds, s => Assert.Equal("asking-the-user", s));
        });
        Assert.All(g.Arrows, a =>
        {
            Assert.Contains(a.SourceAgentId, ids);
            Assert.Contains(a.TargetAgentId, ids);
            if (a.Condition.Type == ConditionType.Regex)
                Regex.IsMatch("", a.Condition.Value);
        });

        var entries = g.Agents.Where(a => g.Arrows.All(x => x.TargetAgentId != a.Id)).ToList();
        Assert.Single(entries);
        Assert.All(g.Agents, a => Assert.True(ArrowRouting.CanReach(g, entries[0].Id, a.Id), $"{a.Name} unreachable"));
        Assert.All(g.Agents.Where(a => a.EnabledTools.Contains(ToolNames.AskUser)), a => Assert.Contains("asking-the-user", a.SkillIds));
    }

    [Fact]
    public void Seed_copies_once_and_does_not_resurrect_deleted_graphs()
    {
        var dir = Directory.CreateTempSubdirectory("ao-seed").FullName;
        try
        {
            var paths = new DataPaths(dir);
            var settings = new AppSettings();
            Assert.True(BuiltInGraphs.Seed(paths, settings));
            Assert.Equal(8, Directory.GetFiles(paths.GraphsDir, "*.json").Length);

            File.Delete(Directory.GetFiles(paths.GraphsDir, "*.json")[0]);
            Assert.False(BuiltInGraphs.Seed(paths, settings));
            Assert.Equal(7, Directory.GetFiles(paths.GraphsDir, "*.json").Length);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static async Task<(RunResult Result, FakeFactory Factory)> RunBuiltIn(string fileName, Action<FakeFactory> script)
    {
        var graph = Get(fileName);
        foreach (var a in graph.Agents) a.ModelId = a.Id;
        var factory = new FakeFactory();
        script(factory);
        var entry = graph.Agents.First(a => graph.Arrows.All(x => x.TargetAgentId != a.Id));
        var work = Directory.CreateTempSubdirectory("ao-builtin").FullName;
        try
        {
            var result = await new GraphRunner(new GraphRunnerOptions
            {
                ChatClientFactory = factory,
                ProviderLookup = _ => new ProviderConfig { Id = "copilot", Name = "c" },
            }).RunAsync(graph, new RunRequest(entry.Id, "topic", work, null));
            return (result, factory);
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    [Fact]
    public async Task Parallel_specialists_fan_in_runs_aggregator_once()
    {
        var (result, f) = await RunBuiltIn("03-parallel-specialists.json", f =>
        {
            foreach (var id in new[] { "scoper", "security", "performance", "maintainability" })
                f.Responders[id] = _ => FakeChatClient.Text(id + " done");
            f.Responders["aggregator"] = _ => FakeChatClient.Text("merged");
        });

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(1, f.CallCount("aggregator"));
        Assert.Equal("merged", Assert.Single(result.FinalOutputs).Output);
    }

    [Fact]
    public async Task Writer_critic_refines_until_score_passes()
    {
        var scores = new Queue<string>(["SCORE: 6/10", "SCORE: 9/10"]);
        var (result, f) = await RunBuiltIn("06-writer-critic.json", f =>
        {
            f.Responders["outliner"] = _ => FakeChatClient.Text("outline");
            f.Responders["drafter"] = _ => FakeChatClient.Text("draft");
            f.Responders["critic"] = _ => { lock (scores) return FakeChatClient.Text("feedback\n" + scores.Dequeue()); };
            f.Responders["publisher"] = _ => FakeChatClient.Text("published");
        });

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(2, f.CallCount("drafter"));
        Assert.Equal(2, f.CallCount("critic"));
        Assert.Equal("published", Assert.Single(result.FinalOutputs).Output);
    }
}
