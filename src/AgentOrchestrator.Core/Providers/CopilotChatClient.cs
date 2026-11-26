using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;

#pragma warning disable GHCP001 // PermissionDecision is marked experimental by the SDK.

namespace AgentOrchestrator.Core.Providers;

/// <summary>
/// Adapts a GitHub Copilot session to <see cref="IChatClient"/>. Copilot runs its own agent loop and calls tools back
/// into this process; each callback is surfaced to the caller as a <see cref="FunctionCallContent"/> and the session
/// waits until the caller sends the matching <see cref="FunctionResultContent"/> in its next request. That keeps the
/// orchestrator's tool loop (approvals, ask_user, events) in charge while Copilot provides the model.
/// Conversations are tracked by the identity of their first message, so callers must reuse the message objects
/// they already sent (as the agent executor does).
/// </summary>
public sealed class CopilotChatClient(CopilotHost host, string? token, string modelId) : IChatClient
{
    private readonly Dictionary<ChatMessage, Conversation> _conversations = new(ReferenceEqualityComparer.Instance);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        if (list.Count == 0)
            throw new ArgumentException("At least one message is required.", nameof(messages));

        Conversation? conversation;
        lock (_conversations)
            _conversations.TryGetValue(list[0], out conversation);

        if (conversation is null)
        {
            conversation = await StartAsync(list, options, cancellationToken);
            lock (_conversations)
                _conversations[list[0]] = conversation;
        }
        else
        {
            conversation.SupplyResults(list);
        }

        await foreach (var update in conversation.ReadTurnAsync(cancellationToken))
            yield return update;

        if (conversation.IsFinished)
        {
            lock (_conversations)
                _conversations.Remove(list[0]);
            await conversation.DisposeAsync();
        }
    }

    private async Task<Conversation> StartAsync(IList<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        var client = await host.GetClientAsync(token, ct);
        var conversation = new Conversation();

        var systemPrompt = string.Join("\n\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        var tools = (options?.Tools ?? []).OfType<AIFunction>().Select(f => (AIFunctionDeclaration)new BridgedFunction(f, conversation)).ToList();

        var config = new SessionConfig
        {
            Model = string.IsNullOrWhiteSpace(modelId) ? null : modelId,
            Streaming = true,
            Tools = tools,
            // Only the orchestrator's tools: no Copilot built-ins, skills, MCP servers or repo instructions.
            AvailableTools = tools.Select(t => t.Name).ToList(),
            EnableSkills = false,
            EnableConfigDiscovery = false,
            SkipCustomInstructions = true,
            EnableHostGitOperations = false,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Replace,
                Content = string.IsNullOrWhiteSpace(systemPrompt) ? "You are a helpful assistant." : systemPrompt,
            },
            // Tool approval is enforced by the orchestrator before it returns a tool result.
            OnPermissionRequest = (_, _) => Task.FromResult<PermissionDecision>(PermissionDecision.ApproveOnce()),
        };

        conversation.Session = await client.CreateSessionAsync(config, ct);
        conversation.Session.On<SessionEvent>(conversation.OnEvent);
        await conversation.Session.SendAsync(new MessageOptions { Prompt = BuildPrompt(messages) }, ct);
        return conversation;
    }

    /// <summary>The first request becomes one prompt: earlier turns (e.g. a handoff) are rendered as a transcript.</summary>
    internal static string BuildPrompt(IList<ChatMessage> messages)
    {
        var turns = messages.Where(m => m.Role != ChatRole.System).ToList();
        if (turns.Count == 1)
            return turns[0].Text;

        var sb = new StringBuilder("Conversation so far:\n");
        foreach (var message in turns.Take(turns.Count - 1))
        {
            sb.Append("\n[").Append(message.Role.Value).Append("]\n");
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                        sb.AppendLine(text.Text);
                        break;
                    case FunctionCallContent call:
                        sb.Append("(called tool ").Append(call.Name).AppendLine(")");
                        break;
                    case FunctionResultContent result:
                        sb.Append("(tool result) ").AppendLine(result.Result?.ToString());
                        break;
                }
            }
        }
        sb.Append("\n---\n").Append(turns[^1].Text);
        return sb.ToString();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
        List<Conversation> open;
        lock (_conversations)
        {
            open = [.. _conversations.Values];
            _conversations.Clear();
        }
        foreach (var conversation in open)
            _ = conversation.DisposeAsync().AsTask();
    }

    private sealed record PendingCall(string CallId, string Name, Dictionary<string, object?> Arguments, TaskCompletionSource<object?> Result);

    private abstract record Item;
    private sealed record TextItem(string Text) : Item;
    private sealed record CallItem(PendingCall Call) : Item;
    private sealed record IdleItem : Item;
    private sealed record ErrorItem(string Message) : Item;

    private sealed class Conversation : IAsyncDisposable
    {
        private readonly Channel<Item> _items = Channel.CreateUnbounded<Item>();
        private readonly Dictionary<string, PendingCall> _pending = [];
        private readonly HashSet<string> _streamedMessageIds = [];
        private int _disposed;

        public CopilotSession? Session { get; set; }
        public bool IsFinished { get; private set; }

        public void OnEvent(SessionEvent evt)
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent { Data: { } d } when !string.IsNullOrEmpty(d.DeltaContent):
                    lock (_streamedMessageIds)
                        _streamedMessageIds.Add(d.MessageId ?? "");
                    _items.Writer.TryWrite(new TextItem(d.DeltaContent));
                    break;
                case AssistantMessageEvent { Data: { } m } when !string.IsNullOrEmpty(m.Content):
                    bool streamed;
                    lock (_streamedMessageIds)
                        streamed = _streamedMessageIds.Contains(m.MessageId ?? "");
                    if (!streamed)
                        _items.Writer.TryWrite(new TextItem(m.Content));
                    break;
                case SessionErrorEvent e:
                    _items.Writer.TryWrite(new ErrorItem(e.Data?.Message ?? "GitHub Copilot reported an error."));
                    break;
                case SessionIdleEvent:
                    _items.Writer.TryWrite(new IdleItem());
                    break;
            }
        }

        public async Task<object?> InvokeToolAsync(string name, AIFunctionArguments arguments, CancellationToken ct)
        {
            var call = new PendingCall(
                "copilot_" + Guid.NewGuid().ToString("N")[..12],
                name,
                arguments.ToDictionary(kv => kv.Key, kv => kv.Value),
                new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously));
            lock (_pending)
                _pending[call.CallId] = call;
            _items.Writer.TryWrite(new CallItem(call));
            using var _ = ct.Register(() => call.Result.TrySetCanceled(ct));
            return await call.Result.Task;
        }

        public void SupplyResults(IList<ChatMessage> messages)
        {
            foreach (var result in messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>())
            {
                PendingCall? call;
                lock (_pending)
                {
                    if (!_pending.Remove(result.CallId, out call))
                        continue;
                }
                call.Result.TrySetResult(result.Result);
            }
        }

        /// <summary>Streams until Copilot needs tool results (returns the calls) or finishes (idle).</summary>
        public async IAsyncEnumerable<ChatResponseUpdate> ReadTurnAsync([EnumeratorCancellation] CancellationToken ct)
        {
            using var abort = ct.Register(() => _ = DisposeAsync().AsTask());
            var sawCall = false;
            while (true)
            {
                Item? item;
                if (sawCall)
                {
                    // Give parallel tool calls a moment to arrive so they're returned together.
                    using var settle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    settle.CancelAfter(TimeSpan.FromMilliseconds(150));
                    try
                    {
                        item = await _items.Reader.ReadAsync(settle.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        yield break;
                    }
                }
                else
                {
                    item = await _items.Reader.ReadAsync(ct);
                }

                switch (item)
                {
                    case TextItem text:
                        yield return new ChatResponseUpdate(ChatRole.Assistant, text.Text);
                        break;
                    case CallItem { Call: var call }:
                        sawCall = true;
                        yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent(call.CallId, call.Name, call.Arguments)]);
                        break;
                    case ErrorItem error:
                        IsFinished = true;
                        throw new InvalidOperationException($"GitHub Copilot: {error.Message}");
                    case IdleItem:
                        lock (_pending)
                        {
                            if (_pending.Count > 0)
                                continue;
                        }
                        IsFinished = true;
                        yield break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            IsFinished = true;
            lock (_pending)
            {
                foreach (var call in _pending.Values)
                    call.Result.TrySetCanceled();
                _pending.Clear();
            }
            if (Session is { } session)
            {
                try { await session.AbortAsync(); } catch { /* session may already be idle or closed */ }
                try { await session.DisposeAsync(); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Declares the orchestrator's tool to Copilot; invocations are parked until the orchestrator answers.</summary>
    private sealed class BridgedFunction(AIFunction inner, Conversation conversation) : DelegatingAIFunction(inner)
    {
        private readonly IReadOnlyDictionary<string, object?> _properties = new Dictionary<string, object?>(inner.AdditionalProperties)
        {
            ["skip_permission"] = true,
            ["overrides_built_in_tool"] = true,
        };

        public override IReadOnlyDictionary<string, object?> AdditionalProperties => _properties;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            new(conversation.InvokeToolAsync(Name, arguments, cancellationToken));
    }
}
