using AgentOrchestrator.Core.Models;

namespace AgentOrchestrator.Core.Engine;

/// <summary>Helpers for rerunning a session in place: its starting run, and deleting the runs it had before.</summary>
public static class SessionRerun
{
    /// <summary>The session's first run, which holds the starting prompt and entry agent (null if it has no runs).</summary>
    public static RunSummary? FirstRun(RunLogger logger, SessionDefinition session) =>
        session.RunIds.Count == 0 ? null : logger.LoadSummary(session.RunIds[0]);

    /// <summary>Deletes all of the session's run logs and clears its run list.</summary>
    public static void DeleteRuns(RunLogger logger, SessionDefinition session)
    {
        foreach (var runId in session.RunIds)
            logger.DeleteRun(runId);
        session.RunIds.Clear();
    }
}
