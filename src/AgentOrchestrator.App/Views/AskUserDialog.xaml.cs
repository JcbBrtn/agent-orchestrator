using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentOrchestrator.Core.Tools;

namespace AgentOrchestrator.App.Views;

public partial class AskUserDialog : Window
{
    public AskUserDialog(UserQuestion question)
    {
        InitializeComponent();
        HeaderText.Text = $"❓ {question.AgentName} is asking:";
        QuestionText.Text = question.Question;
        OptionsList.ItemsSource = question.Options;
        OptionsPanel.Visibility = question.Options.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AnswerBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                OnAnswer(this, e);
            }
        };
        Loaded += (_, _) => AnswerBox.Focus();
    }

    /// <summary>The user's answer, or null when skipped.</summary>
    public string? Answer { get; private set; }

    private void OnOption(object sender, RoutedEventArgs e)
    {
        Answer = (string)((Button)sender).Content;
        DialogResult = true;
    }

    private void OnAnswer(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AnswerBox.Text))
        {
            AnswerBox.Focus();
            return;
        }
        Answer = AnswerBox.Text.Trim();
        DialogResult = true;
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        Answer = null;
        DialogResult = false;
    }
}
