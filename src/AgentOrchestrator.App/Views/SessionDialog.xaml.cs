using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AgentOrchestrator.App.Views;

/// <summary>An orchestration the session can use. A null path means the current (unsaved) graph in the editor.</summary>
public sealed record GraphChoice(string Display, string? Path)
{
    public string ShortName => Path is null ? "Current graph (unsaved)" : System.IO.Path.GetFileNameWithoutExtension(Path);
}

public partial class SessionDialog : Window
{
    private string _autoName = "";

    /// <param name="graphs">Choices to offer; <paramref name="selected"/> is pre-selected.</param>
    public SessionDialog(IReadOnlyList<GraphChoice> graphs, GraphChoice? selected, string? folder, string? name, bool isEdit)
    {
        InitializeComponent();
        Title = isEdit ? "Edit session" : "New session";
        OkButton.Content = isEdit ? "Save" : "Create";
        GraphCombo.ItemsSource = graphs.ToList();
        GraphCombo.SelectedItem = selected ?? graphs.FirstOrDefault();
        NameBox.Text = name ?? "";
        _autoName = name ?? "";
        FolderBox.Text = folder ?? "";
        Loaded += (_, _) => (string.IsNullOrEmpty(FolderBox.Text) ? FolderBox : (Control)NameBox).Focus();
    }

    public string SessionName => NameBox.Text.Trim();
    public string WorkingDirectory => FolderBox.Text.Trim();
    public GraphChoice? Graph => GraphCombo.SelectedItem as GraphChoice;

    private void OnFolderChanged(object sender, TextChangedEventArgs e)
    {
        // Keep the name in sync with the folder until the user types their own.
        if (NameBox.Text.Trim() != _autoName) return;
        var folderName = Path.GetFileName(WorkingDirectory.TrimEnd('\\', '/'));
        _autoName = string.IsNullOrEmpty(folderName) ? WorkingDirectory : folderName;
        NameBox.Text = _autoName;
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the session's working directory",
            InitialDirectory = Directory.Exists(WorkingDirectory) ? WorkingDirectory : null,
        };
        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private void OnBrowseGraph(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose an orchestration", Filter = "Agent graph (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        var items = (List<GraphChoice>)GraphCombo.ItemsSource;
        var choice = items.FirstOrDefault(g => string.Equals(g.Path, dialog.FileName, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
        {
            choice = new GraphChoice(Path.GetFileNameWithoutExtension(dialog.FileName) + "  —  " + dialog.FileName, dialog.FileName);
            items.Add(choice);
            GraphCombo.Items.Refresh();
        }
        GraphCombo.SelectedItem = choice;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(WorkingDirectory))
        {
            MessageBox.Show(this, "Choose an existing working directory.", Title);
            return;
        }
        if (Graph is null)
        {
            MessageBox.Show(this, "Choose an orchestration.", Title);
            return;
        }
        if (string.IsNullOrWhiteSpace(SessionName))
            NameBox.Text = Path.GetFileName(WorkingDirectory.TrimEnd('\\', '/'));
        DialogResult = true;
    }
}
