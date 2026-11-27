using System.IO;
using System.Windows;
using AgentOrchestrator.App.ViewModels;
using Microsoft.Win32;

namespace AgentOrchestrator.App.Views;

public partial class RunDialog : Window
{
    public RunDialog(IEnumerable<AgentNodeViewModel> agents, AgentNodeViewModel defaultEntry, string? workingDirectory, bool lockWorkingDirectory = false)
    {
        InitializeComponent();
        DataContext = new RunDialogViewModel(agents, defaultEntry, workingDirectory, lockWorkingDirectory);

        if (lockWorkingDirectory)
        {
            WorkDirLabel.Text = "Working directory (from the session; use Edit session to change)";
            WorkDirBox.IsReadOnly = true;
            BrowseButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            Title = "Run graph — starts a new session";
            WorkDirLabel.Text = "Working directory for the new session (default for relative paths and PowerShell)";
        }

        EntryCombo.ItemsSource = agents.ToList();
        EntryCombo.SelectedItem = defaultEntry;
        if (DataContext is RunDialogViewModel vm)
        {
            WorkDirBox.Text = vm.WorkingDirectory ?? "";
        }
        
        Loaded += (_, _) => TaskBox.Focus();
    }

    public AgentNodeViewModel EntryAgent => (AgentNodeViewModel)EntryCombo.SelectedItem;
    public string WorkingDirectory => WorkDirBox.Text.Trim();
    public string TaskText => TaskBox.Text.Trim();

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(WorkingDirectory) ? WorkingDirectory : null };
        if (dialog.ShowDialog(this) == true)
            WorkDirBox.Text = dialog.FolderName;
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if (EntryCombo.SelectedItem is null)
        {
            MessageBox.Show(this, "Choose an entry agent.", Title);
            return;
        }
        if (!Directory.Exists(WorkingDirectory))
        {
            MessageBox.Show(this, "The working directory does not exist.", Title);
            return;
        }
        if (string.IsNullOrWhiteSpace(TaskText))
        {
            MessageBox.Show(this, "Enter a task.", Title);
            return;
        }
        DialogResult = true;
    }
}
