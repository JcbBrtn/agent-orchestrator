namespace AgentOrchestrator.Core.Tools;

public enum ApprovalDecision
{
    Approve,
    Deny,
    /// <summary>Approve this call and all future calls of this tool for this agent during the current run.</summary>
    ApproveAllForRun,
}

public record ApprovalRequest(string AgentId, string AgentName, string ToolName, string ArgumentsJson, bool IsRisky);

public interface IApprovalService
{
    Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

public sealed class AutoApproveService : IApprovalService
{
    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovalDecision.Approve);
}

public record UserQuestion(string AgentId, string AgentName, string Question, IReadOnlyList<string> Options);

/// <summary>Lets an agent ask the human a question mid-run. Returns null when the user skips (agent should decide itself).</summary>
public interface IUserInputService
{
    Task<string?> AskAsync(UserQuestion question, CancellationToken cancellationToken);
}

/// <summary>Used when nobody is available to answer (tests, headless runs).</summary>
public sealed class NoUserInputService : IUserInputService
{
    public Task<string?> AskAsync(UserQuestion question, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
