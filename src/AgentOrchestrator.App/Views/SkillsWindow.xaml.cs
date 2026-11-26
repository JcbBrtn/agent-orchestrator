using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AgentOrchestrator.App.Services;
using AgentOrchestrator.Core.Models;

namespace AgentOrchestrator.App.Views;

public partial class SkillsWindow : Window
{
    private readonly AppServices _services = AppServices.Instance;
    private SkillDefinition? _current;

    public SkillsWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        Reload(null);
    }

    private void Reload(string? selectId)
    {
        var skills = _services.Skills.LoadAll();
        SkillList.ItemsSource = skills;
        SkillList.SelectedItem = skills.FirstOrDefault(s => s.Id == selectId) ?? skills.FirstOrDefault();
        if (SkillList.SelectedItem is null) Show(null);
    }

    private void Show(SkillDefinition? skill)
    {
        _current = skill;
        EditorPanel.IsEnabled = skill is not null;
        NameBox.Text = skill?.Name ?? "";
        DescriptionBox.Text = skill?.Description ?? "";
        ContentBox.Text = skill?.Content ?? "";
        FileText.Text = string.IsNullOrEmpty(skill?.Id) ? "(not saved yet)" : skill.Id + ".md";
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SkillList.SelectedItem is SkillDefinition s) Show(s);
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        SkillList.SelectedItem = null;
        Show(new SkillDefinition { Name = "New skill", Content = "Describe how the agent should behave when using this skill.\n" });
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show(this, "Name is required.", Title);
            return;
        }
        _current.Name = NameBox.Text.Trim();
        _current.Description = DescriptionBox.Text.Trim().Replace('\n', ' ').Replace("\r", "");
        _current.Content = ContentBox.Text;
        _services.Skills.Save(_current);
        _services.RaiseSkillsChanged();
        Reload(_current.Id);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (SkillList.SelectedItem is not SkillDefinition s) return;
        if (MessageBox.Show(this, $"Delete skill '{s.Name}'?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _services.Skills.Delete(s.Id);
        _services.RaiseSkillsChanged();
        Reload(null);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_services.Paths.SkillsDir) { UseShellExecute = true });
}
