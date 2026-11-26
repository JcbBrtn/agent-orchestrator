using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AgentOrchestrator.Core.Models;
using AgentOrchestrator.Core.Providers;
using Microsoft.Extensions.AI;

namespace AgentOrchestrator.Core.Tests;

/// <summary>Scripted chat client: the responder is chosen by model id, so each test agent uses its own model id.</summary>
public sealed class FakeChatClient(Func<IList<ChatMessage>, ChatResponse> responder) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(responder(messages.ToList()));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        foreach (var update in responder(messages.ToList()).ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }

    public static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    public static ChatResponse Call(string name, Dictionary<string, object?> args) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args)]));
}

public sealed class FakeFactory : IChatClientFactory
{
    public ConcurrentDictionary<string, Func<IList<ChatMessage>, ChatResponse>> Responders { get; } = new();
    public ConcurrentDictionary<string, List<IList<ChatMessage>>> Calls { get; } = new();

    public IChatClient Create(ProviderConfig provider, string modelId) =>
        new FakeChatClient(messages =>
        {
            var calls = Calls.GetOrAdd(modelId, _ => []);
            lock (calls) calls.Add(messages);
            return Responders[modelId](messages);
        });

    public int CallCount(string modelId) => Calls.TryGetValue(modelId, out var c) ? c.Count : 0;
}
