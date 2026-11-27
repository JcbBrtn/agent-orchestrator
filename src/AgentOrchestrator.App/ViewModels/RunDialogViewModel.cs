using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using AgentOrchestrator.Core.Models;

namespace AgentOrchestrator.App.ViewModels;

public class RunDialogViewModel : INotifyPropertyChanged
{
    private AgentNodeViewModel? _entryAgent;
    private string? _workingDirectory;
    private string? _taskText;
    private float? _temperature;
    private float? _topP;
    private int? _topK;

    public AgentNodeViewModel? EntryAgent
    {
        get => _entryAgent;
        set
        {
            _entryAgent = value;
            OnPropertyChanged();
        }
    }

    public string? WorkingDirectory
    {
        get => _workingDirectory;
        set
        {
            _workingDirectory = value;
            OnPropertyChanged();
        }
    }

    public string? TaskText
    {
        get => _taskText;
        set
        {
            _taskText = value;
            OnPropertyChanged();
        }
    }

    public float? Temperature
    {
        get => _temperature;
        set
        {
            _temperature = value;
            OnPropertyChanged();
        }
    }

    public float? TopP
    {
        get => _topP;
        set
        {
            _topP = value;
            OnPropertyChanged();
        }
    }

    public int? TopK
    {
        get => _topK;
        set
        {
            _topK = value;
            OnPropertyChanged();
        }
    }

    public RunDialogViewModel(IEnumerable<AgentNodeViewModel> agents, AgentNodeViewModel? defaultEntry, string? workingDirectory, bool lockWorkingDirectory = false)
    {
        _entryAgent = defaultEntry;
        _workingDirectory = workingDirectory;
        
        if (_entryAgent != null)
        {
            Temperature = _entryAgent.Model.Temperature;
            TopP = _entryAgent.Model.TopP;
            TopK = _entryAgent.Model.TopK;
        }

        if (string.IsNullOrEmpty(_workingDirectory))
        {
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
