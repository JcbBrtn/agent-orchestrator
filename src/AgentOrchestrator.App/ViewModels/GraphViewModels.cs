using System.Collections.ObjectModel;
using System.Windows;
using AgentOrchestrator.App.Services;
using AgentOrchestrator.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentOrchestrator.App.ViewModels;

public enum NodeRunState
{
    Idle,
    Running,
    WaitingApproval,
    WaitingInput,
    Done,
    Error,
}

public partial class ConnectorViewModel(AgentNodeViewModel node, bool isInput) : ObservableObject
{
    public AgentNodeViewModel Node { get; } = node;
    public bool IsInput { get; } = isInput;
    public string Title => IsInput ? "in" : "out";

    [ObservableProperty] private Point _anchor;
    [ObservableProperty] private bool _isConnected;
}

public partial class ToggleItemViewModel(string id, string label, string description, bool isChecked, Action onChanged) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public string Description { get; } = description;

    [ObservableProperty] private bool _isChecked = isChecked;

    partial void OnIsCheckedChanged(bool value) => onChanged();
}

public partial class AgentNodeViewModel : ObservableObject
{
    public AgentNodeViewModel(AgentDefinition model)
    {
        Model = model;
        Input = new ConnectorViewModel(this, isInput: true);
        Output = new ConnectorViewModel(this, isInput: false);
        _location = new Point(model.X, model.Y);
        Tools = new ObservableCollection<ToggleItemViewModel>(ToolNames.All.Select(t =>
            new ToggleItemViewModel(t, t, ToolDescription(t), model.EnabledTools.Contains(t), SyncTools)));
        Skills = [];
        RefreshSkills();
        AvailableModels = string.IsNullOrEmpty(model.ModelId) ? [] : [model.ModelId];
    }

    public AgentDefinition Model { get; }
    public ConnectorViewModel Input { get; }
    public ConnectorViewModel Output { get; }
    public IReadOnlyList<ConnectorViewModel> Inputs => [Input];
    public IReadOnlyList<ConnectorViewModel> Outputs => [Output];
    public ObservableCollection<ToggleItemViewModel> Tools { get; }
    public ObservableCollection<ToggleItemViewModel> Skills { get; }
    public IReadOnlyList<ProviderConfig> Providers => AppServices.Instance.Settings.Providers;
    public static ApprovalMode[] ApprovalModes { get; } = Enum.GetValues<ApprovalMode>();
    public static JoinMode[] JoinModes { get; } = Enum.GetValues<JoinMode>();

    [ObservableProperty] private Point _location;
    [ObservableProperty] private NodeRunState _runState;
    [ObservableProperty] private string? _modelsStatus;
    [ObservableProperty] private ObservableCollection<string> _availableModels;

    partial void OnLocationChanged(Point value)
    {
        Model.X = value.X;
        Model.Y = value.Y;
    }

    public string Id => Model.Id;

    public string Name
    {
        get => Model.Name;
        set => SetProperty(Model.Name, value, Model, (m, v) => m.Name = v);
    }

    public string ProviderId
    {
        get => Model.ProviderId;
        set
        {
            if (SetProperty(Model.ProviderId, value, Model, (m, v) => m.ProviderId = v))
            {
                OnPropertyChanged(nameof(Subtitle));
                _ = RefreshModelsAsync();
            }
        }
    }

    public string ModelId
    {
        get => Model.ModelId;
        set
        {
            if (SetProperty(Model.ModelId, value ?? "", Model, (m, v) => m.ModelId = v))
                OnPropertyChanged(nameof(Subtitle));
        }
    }

    public string SystemPrompt
    {
        get => Model.SystemPrompt;
        set => SetProperty(Model.SystemPrompt, value, Model, (m, v) => m.SystemPrompt = v);
    }

    public string TemperatureText
    {
        get => Model.Temperature?.ToString() ?? "";
        set => SetProperty(TemperatureText, value, Model, (m, v) => m.Temperature = float.TryParse(v, out var f) ? f : null);
    }

    public string TopPText
    {
        get => Model.TopP?.ToString() ?? "";
        set => SetProperty(TopPText, value, Model, (m, v) => m.TopP = float.TryParse(v, out var f) ? f : null);
    }

    public string TopKText
    {
        get => Model.TopK?.ToString() ?? "";
        set => SetProperty(TopKText, value, Model, (m, v) => m.TopK = int.TryParse(v, out var i) ? i : null);
    }

    public ApprovalMode ApprovalMode
    {
        get => Model.ApprovalMode;
        set => SetProperty(Model.ApprovalMode, value, Model, (m, v) => m.ApprovalMode = v);
    }

    public JoinMode JoinMode
    {
        get => Model.JoinMode;
        set => SetProperty(Model.JoinMode, value, Model, (m, v) => m.JoinMode = v);
    }

    public int MaxToolIterations
    {
        get => Model.MaxToolIterations;
        set => SetProperty(Model.MaxToolIterations, value, Model, (m, v) => m.MaxToolIterations = v);
    }

    public bool KeepLoopMemory
    {
        get => Model.KeepLoopMemory;
        set => SetProperty(Model.KeepLoopMemory, value, Model, (m, v) => m.KeepLoopMemory = v);
    }

    public int MemoryMaxMessages
    {
        get => Model.MemoryMaxMessages;
        set => SetProperty(Model.MemoryMaxMessages, value, Model, (m, v) => m.MemoryMaxMessages = v);
    }

    public string Subtitle
    {
        get
        {
            var provider = AppServices.Instance.FindProvider(ProviderId)?.Name ?? "(no provider)";
            return string.IsNullOrEmpty(ModelId) ? provider : $"{provider} · {ModelId}";
        }
    }

    public string ToolSummary
    {
        get
        {
            var tools = Model.EnabledTools.Count == 0 ? "no tools" : string.Join(", ", Model.EnabledTools);
            return Model.SkillIds.Count == 0 ? tools : $"{tools} · {Model.SkillIds.Count} skill(s)";
        }
    }

    public void RefreshSkills()
    {
        Skills.Clear();
        foreach (var skill in AppServices.Instance.Skills.LoadAll())
            Skills.Add(new ToggleItemViewModel(skill.Id, skill.Name, skill.Description, Model.SkillIds.Contains(skill.Id), SyncSkills));
    }

    public void RefreshProviders() => OnPropertyChanged(nameof(Providers));

    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        var provider = AppServices.Instance.FindProvider(ProviderId);
        if (provider is null)
        {
            ModelsStatus = "Select a provider.";
            return;
        }
        ModelsStatus = "Loading models…";
        try
        {
            var models = await AppServices.Instance.Models.ListModelsAsync(provider);
            var current = ModelId;
            AvailableModels = new ObservableCollection<string>(models);
            if (!string.IsNullOrEmpty(current) && !AvailableModels.Contains(current))
                AvailableModels.Insert(0, current);
            ModelId = current;
            OnPropertyChanged(nameof(ModelId));
            ModelsStatus = $"{models.Count} model(s) available.";
        }
        catch (Exception ex)
        {
            ModelsStatus = $"Could not list models: {ex.GetBaseException().Message}";
        }
    }

    private void SyncTools()
    {
        Model.EnabledTools = Tools.Where(t => t.IsChecked).Select(t => t.Id).ToList();
        OnPropertyChanged(nameof(ToolSummary));
    }

    private void SyncSkills()
    {
        Model.SkillIds = Skills.Where(s => s.IsChecked).Select(s => s.Id).ToList();
        OnPropertyChanged(nameof(ToolSummary));
    }

    private static string ToolDescription(string tool) => tool switch
    {
        ToolNames.ReadFile => "Read files",
        ToolNames.CreateFile => "Create / overwrite files",
        ToolNames.EditFile => "Edit files (string replace)",
        ToolNames.ListDirectory => "List directories",
        ToolNames.SearchFiles => "Regex search in files",
        ToolNames.WebRequest => "HTTP requests",
        ToolNames.RunPowerShell => "Run PowerShell scripts",
        ToolNames.AskUser => "Ask the user questions",
        _ => tool,
    };
}

public partial class ArrowViewModel : ObservableObject
{
    public ArrowViewModel(ArrowDefinition model, ConnectorViewModel source, ConnectorViewModel target)
    {
        Model = model;
        Source = source;
        Target = target;
        source.IsConnected = true;
        target.IsConnected = true;
        source.PropertyChanged += OnAnchorChanged;
        target.PropertyChanged += OnAnchorChanged;
    }

    /// <summary>True when the arrow points backwards (right-to-left), e.g. a loop; drawn with a distinct path so labels don't collide.</summary>
    public bool IsBackEdge => Target.Anchor.X < Source.Anchor.X;

    private void OnAnchorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectorViewModel.Anchor)) OnPropertyChanged(nameof(IsBackEdge));
    }

    public ArrowDefinition Model { get; }
    public ConnectorViewModel Source { get; }
    public ConnectorViewModel Target { get; }
    public static ArrowMode[] Modes { get; } = Enum.GetValues<ArrowMode>();
    public static ConditionType[] ConditionTypes { get; } = Enum.GetValues<ConditionType>();

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private int _traversalCount;

    public string Header => $"{Source.Node.Name} → {Target.Node.Name}";

    public string Name
    {
        get => Model.Name;
        set { if (SetProperty(Model.Name, value, Model, (m, v) => m.Name = v)) OnPropertyChanged(nameof(Label)); }
    }

    public string Description
    {
        get => Model.Description;
        set => SetProperty(Model.Description, value, Model, (m, v) => m.Description = v);
    }

    public ArrowMode Mode
    {
        get => Model.Mode;
        set
        {
            if (SetProperty(Model.Mode, value, Model, (m, v) => m.Mode = v))
            {
                OnPropertyChanged(nameof(Label));
                OnPropertyChanged(nameof(PromptLabel));
            }
        }
    }

    public string PromptLabel => Mode == ArrowMode.Template
        ? "Prompt template (sent to target)"
        : "Added context (appended after handed-off conversation)";

    public string Prompt
    {
        get => Model.Prompt;
        set => SetProperty(Model.Prompt, value, Model, (m, v) => m.Prompt = v);
    }

    public ConditionType ConditionType
    {
        get => Model.Condition.Type;
        set
        {
            if (SetProperty(Model.Condition.Type, value, Model, (m, v) => m.Condition.Type = v))
            {
                OnPropertyChanged(nameof(Label));
                OnPropertyChanged(nameof(ConditionNeedsValue));
            }
        }
    }

    public bool ConditionNeedsValue => ConditionType is ConditionType.Keyword or ConditionType.Regex;

    public string ConditionValue
    {
        get => Model.Condition.Value;
        set { if (SetProperty(Model.Condition.Value, value, Model, (m, v) => m.Condition.Value = v)) OnPropertyChanged(nameof(Label)); }
    }

    public bool Negate
    {
        get => Model.Condition.Negate;
        set { if (SetProperty(Model.Condition.Negate, value, Model, (m, v) => m.Condition.Negate = v)) OnPropertyChanged(nameof(Label)); }
    }

    public int MaxTraversals
    {
        get => Model.MaxTraversals;
        set => SetProperty(Model.MaxTraversals, value, Model, (m, v) => m.MaxTraversals = v);
    }

    public string Label
    {
        get
        {
            var not = Negate ? "not " : "";
            var condition = ConditionType switch
            {
                ConditionType.Always => Negate ? " [never]" : "",
                ConditionType.Keyword => $" [{not}\"{ConditionValue}\"]",
                ConditionType.Regex => $" [{not}/{ConditionValue}/]",
                ConditionType.AgentChoice => " [agent choice]",
                _ => "",
            };
            var mode = Mode == ArrowMode.Handoff ? " ⇒" : "";
            return $"{Name}{condition}{mode}";
        }
    }

    public void RefreshHeader() => OnPropertyChanged(nameof(Header));
}

public partial class PendingConnectionViewModel(MainViewModel editor) : ObservableObject
{
    [ObservableProperty] private ConnectorViewModel? _source;

    [RelayCommand]
    private void Start(ConnectorViewModel? connector) => Source = connector;

    [RelayCommand]
    private void Finish(object? target)
    {
        if (Source is not null && target is ConnectorViewModel connector)
            editor.Connect(Source, connector);
        Source = null;
    }
}

