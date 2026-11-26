using AgentOrchestrator.Core.Engine;
using AgentOrchestrator.Core.Models;
using AgentOrchestrator.Core.Persistence;
using AgentOrchestrator.Core.Providers;
using AgentOrchestrator.Core.Security;

namespace AgentOrchestrator.App.Services;

/// <summary>Application-wide service locator (kept deliberately simple; no DI container).</summary>
public sealed class AppServices
{
    public static AppServices Instance { get; } = new();

    private AppServices()
    {
        Paths = new DataPaths();
        Graphs = new GraphStore(Paths);
        Skills = new SkillStore(Paths);
        SettingsStore = new SettingsStore(Paths);
        Settings = SettingsStore.Load();
        Secrets = new DpapiSecretStore(Paths.SecretsFile);
        Copilot = new CopilotHost(System.IO.Path.Combine(Paths.Root, "copilot"));
        ChatClients = new OpenAICompatibleChatClientFactory(Secrets, Copilot);
        Models = new ModelCatalog(Secrets, copilot: Copilot);
        RunLogger = new RunLogger(Paths.RunsDir);
        Approval = new WpfApprovalService();
        UserInput = new WpfUserInputService();
        Sessions = new SessionStore(Paths);
        MigrateProviders();
        SeedBuiltInSkills();
        if (BuiltInGraphs.Seed(Paths, Settings))
            SaveSettings();
    }

    public DataPaths Paths { get; }
    public GraphStore Graphs { get; }
    public SkillStore Skills { get; }
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; }
    public ISecretStore Secrets { get; }
    public CopilotHost Copilot { get; }
    public IChatClientFactory ChatClients { get; }
    public ModelCatalog Models { get; }
    public RunLogger RunLogger { get; }
    public WpfApprovalService Approval { get; }
    public WpfUserInputService UserInput { get; }
    public SessionStore Sessions { get; }

    public const string AskingSkillId = "asking-the-user";

    public event EventHandler? ProvidersChanged;
    public event EventHandler? SkillsChanged;

    public ProviderConfig? FindProvider(string id) => Settings.Providers.FirstOrDefault(p => p.Id == id);

    public void SaveSettings() => SettingsStore.Save(Settings);

    public void RaiseProvidersChanged() => ProvidersChanged?.Invoke(this, EventArgs.Empty);

    public void RaiseSkillsChanged() => SkillsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Adds the GitHub Copilot provider to existing settings and labels the retired GitHub Models one.</summary>
    private void MigrateProviders()
    {
        var changed = false;
        if (!Settings.Providers.Any(p => p.Kind == ProviderKind.GitHubCopilot))
        {
            var copilot = ProviderConfig.CreateCopilot();
            if (Settings.Providers.Any(p => p.Id == copilot.Id))
                copilot.Id = Guid.NewGuid().ToString("N");
            Settings.Providers.Insert(0, copilot);
            changed = true;
        }
        foreach (var p in Settings.Providers.Where(p => p.Kind == ProviderKind.GitHubModels && !p.Name.Contains("retired", StringComparison.OrdinalIgnoreCase)))
        {
            p.Name += " (retired)";
            changed = true;
        }
        if (changed)
            SaveSettings();
    }

    /// <summary>Writes built-in skills that are missing (users may edit or delete them afterwards; edits are kept).</summary>
    private void SeedBuiltInSkills()
    {
        if (Skills.TryGet(AskingSkillId) is not null)
            return;
        Skills.Save(new SkillDefinition
        {
            Id = AskingSkillId,
            Name = "Asking the user",
            Description = "When and how to use the ask_user tool to clarify requirements",
            Content = """
                You can ask the human user questions with the `ask_user` tool. The run pauses until they answer.

                Ask when:
                - The task is ambiguous and different interpretations would lead to meaningfully different work.
                - You are about to do something destructive or hard to undo (deleting files, overwriting work, running risky commands) and intent is unclear.
                - You need information only the user has (preferences, credentials location, business rules, priorities).

                Do NOT ask when:
                - You can find the answer yourself by reading files, listing directories, or searching.
                - The choice is minor or easily changed later; pick a sensible default and mention it.
                - You already asked the same thing; use the earlier answer.

                How to ask:
                - Ask one concise, specific question at a time.
                - Offer 2-4 likely answers in `options` when possible, with the recommended one first.
                - Briefly give the context needed to answer (what you found, why it matters).
                - If the user skips the question, make a reasonable assumption, state it, and continue.
                """,
        });
    }
}
