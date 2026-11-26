using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.Core.Engine;
using AgentOrchestrator.Core.Models;
using AgentOrchestrator.Core.Providers;
using AgentOrchestrator.Core.Security;

namespace AgentOrchestrator.Core.Tests;

/// <summary>Exercises the real OpenAI-compatible client (SSE streaming + tool calls) against an in-process mock server.</summary>
public sealed class OpenAICompatibleIntegrationTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _baseUrl;
    private readonly string _workDir = Directory.CreateTempSubdirectory("ao-int").FullName;
    private readonly List<JsonNode> _requests = [];

    public OpenAICompatibleIntegrationTests()
    {
        var port = FreePort();
        _baseUrl = $"http://localhost:{port}/v1";
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        _listener.Close();
        Directory.Delete(_workDir, true);
    }

    [Fact]
    public async Task Streams_tool_call_executes_tool_and_returns_final_text()
    {
        var provider = new ProviderConfig { Id = "mock", Name = "Mock", Kind = ProviderKind.OpenAICompatible, Endpoint = _baseUrl };
        var agent = new AgentDefinition { Id = "a", Name = "Writer", ProviderId = "mock", ModelId = "mock-model", EnabledTools = [ToolNames.CreateFile], ApprovalMode = ApprovalMode.Auto };
        var graph = new GraphDefinition { Agents = [agent] };
        var tokens = new StringBuilder();

        var runner = new GraphRunner(new GraphRunnerOptions
        {
            ChatClientFactory = new OpenAICompatibleChatClientFactory(new NullSecrets()),
            ProviderLookup = _ => provider,
        });
        var result = await runner.RunAsync(graph, new RunRequest("a", "write hello", _workDir),
            e => { if (e.Kind == RunEventKind.Token) lock (tokens) tokens.Append(e.Text); });

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal("hello world", File.ReadAllText(Path.Combine(_workDir, "hello.txt")));
        Assert.Equal("All done.", Assert.Single(result.FinalOutputs).Output);
        Assert.Equal("All done.", tokens.ToString());

        lock (_requests)
        {
            Assert.Equal(2, _requests.Count);
            Assert.Equal("mock-model", (string?)_requests[0]["model"]);
            Assert.Contains("create_file", _requests[0]["tools"]!.ToJsonString());
            Assert.Contains(_requests[1]["messages"]!.AsArray(), m => (string?)m!["role"] == "tool");
        }
    }

    [Fact]
    public async Task Model_catalog_lists_models()
    {
        var provider = new ProviderConfig { Kind = ProviderKind.Ollama, Endpoint = _baseUrl };
        var models = await new ModelCatalog(new NullSecrets()).ListModelsAsync(provider);
        Assert.Equal(["a-model", "b-model"], models);
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            try { await HandleAsync(ctx); } catch { /* test server */ }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        if (path.EndsWith("/models"))
        {
            await Write(ctx, "application/json", """{"object":"list","data":[{"id":"b-model"},{"id":"a-model"}]}""");
            return;
        }

        var body = JsonNode.Parse(await new StreamReader(ctx.Request.InputStream).ReadToEndAsync())!;
        lock (_requests) _requests.Add(body);
        var hasToolResult = body["messages"]!.AsArray().Any(m => (string?)m!["role"] == "tool");

        var chunks = new List<string>();
        if (!hasToolResult)
        {
            var args = JsonSerializer.Serialize(new { path = "hello.txt", content = "hello world" });
            chunks.Add(Chunk(new { role = "assistant", tool_calls = new[] { new { index = 0, id = "call_1", type = "function", function = new { name = "create_file", arguments = args[..10] } } } }, null));
            chunks.Add(Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = args[10..] } } } }, null));
            chunks.Add(Chunk(new { }, "tool_calls"));
        }
        else
        {
            chunks.Add(Chunk(new { role = "assistant", content = "All " }, null));
            chunks.Add(Chunk(new { content = "done." }, null));
            chunks.Add(Chunk(new { }, "stop"));
        }
        var sse = string.Concat(chunks.Select(c => $"data: {c}\n\n")) + "data: [DONE]\n\n";
        await Write(ctx, "text/event-stream", sse);
    }

    private static string Chunk(object delta, string? finish) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-1", @object = "chat.completion.chunk", created = 1, model = "mock-model",
        choices = new[] { new { index = 0, delta, finish_reason = finish } },
    });

    private static async Task Write(HttpListenerContext ctx, string contentType, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class NullSecrets : ISecretStore
    {
        public string? Get(string name) => null;
        public void Set(string name, string? value) { }
    }
}
