using System.Text.Json;
using System.Windows;
using AgentOrchestrator.Core.Tools;

namespace AgentOrchestrator.App.Views;

public partial class ApprovalDialog : Window
{
    public ApprovalDialog(ApprovalRequest request)
    {
        InitializeComponent();
        HeaderText.Text = $"Agent '{request.AgentName}' wants to call {request.ToolName}:";
        RiskText.Visibility = request.IsRisky ? Visibility.Visible : Visibility.Collapsed;
        ApproveAllButton.Content = $"Approve all {request.ToolName.Replace("_", "__")} for this agent/run";
        ArgsBox.Text = FormatArguments(request.ArgumentsJson);
    }

    public ApprovalDecision Decision { get; private set; } = ApprovalDecision.Deny;

    /// <summary>Shows arguments as "name:" blocks so multi-line scripts and file contents are readable.</summary>
    private static string FormatArguments(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return json;
            return string.Join(Environment.NewLine + Environment.NewLine, doc.RootElement.EnumerateObject().Select(p =>
                $"{p.Name}:{Environment.NewLine}{(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())}"));
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private void OnApprove(object sender, RoutedEventArgs e) => Close(ApprovalDecision.Approve);
    private void OnApproveAll(object sender, RoutedEventArgs e) => Close(ApprovalDecision.ApproveAllForRun);
    private void OnDeny(object sender, RoutedEventArgs e) => Close(ApprovalDecision.Deny);

    private void Close(ApprovalDecision decision)
    {
        Decision = decision;
        DialogResult = decision != ApprovalDecision.Deny;
    }
}
