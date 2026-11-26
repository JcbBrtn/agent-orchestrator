using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentOrchestrator.App.ViewModels;

/// <summary>A graph file listed under Orchestrations in the left panel.</summary>
public partial class OrchestrationItemViewModel(string path, string name, int agentCount, bool inGraphsDir, string description = "", IReadOnlyList<AgentOrchestrator.Core.Models.GraphReference>? references = null) : ObservableObject
{
    public string Path { get; } = path;
    public string Name { get; } = name;
    public int AgentCount { get; } = agentCount;
    public string Description { get; } = description;
    public IReadOnlyList<AgentOrchestrator.Core.Models.GraphReference> References { get; } = references ?? [];
    public bool HasReferences => References.Count > 0;
    public string ToolTipText => string.IsNullOrWhiteSpace(Description) ? Name : Description;

    /// <summary>True for files in the app's graphs folder (deletable); false for files elsewhere (removable from the list).</summary>
    public bool InGraphsDir { get; } = inGraphsDir;

    [ObservableProperty] private bool _isOpen;

    public string FileName => System.IO.Path.GetFileName(Path);
    public string Details => $"{(AgentCount == 1 ? "1 agent" : $"{AgentCount} agents")} · {FileName}";

    public override string ToString() => Name;
}
