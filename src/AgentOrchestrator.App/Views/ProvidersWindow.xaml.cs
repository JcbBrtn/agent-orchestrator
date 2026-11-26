using System.Windows;
using System.Windows.Controls;
using AgentOrchestrator.App.Services;
using AgentOrchestrator.Core.Models;

namespace AgentOrchestrator.App.Views;

public partial class ProvidersWindow : Window
{
    private readonly AppServices _services = AppServices.Instance;
    private ProviderConfig? _current;
    private bool _loading;

    public ProvidersWindow()
    {
        InitializeComponent();
        KindCombo.ItemsSource = Enum.GetValues<ProviderKind>();
        Reload(null);
    }

    private void Reload(string? selectId)
    {
        ProviderList.ItemsSource = null;
        ProviderList.ItemsSource = _services.Settings.Providers;
        ProviderList.SelectedItem = _services.Settings.Providers.FirstOrDefault(p => p.Id == selectId)
            ?? _services.Settings.Providers.FirstOrDefault();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _current = ProviderList.SelectedItem as ProviderConfig;
        EditorPanel.IsEnabled = _current is not null;
        _loading = true;
        NameBox.Text = _current?.Name ?? "";
        KindCombo.SelectedItem = _current?.Kind;
        EndpointBox.Text = _current?.Endpoint ?? "";
        KeyBox.Password = "";
        var hasKey = _current?.SecretName is { Length: > 0 } name && _services.Secrets.Get(name) is { Length: > 0 };
        KeyHint.Text = hasKey ? "A key is stored. Leave blank to keep it; type a new one to replace it." : "No key stored (local providers usually don't need one).";
        TestResult.Text = "";
        UpdateKindUi(_current?.Kind);
        _loading = false;
    }

    private void UpdateKindUi(ProviderKind? kind)
    {
        var copilot = kind == ProviderKind.GitHubCopilot;
        EndpointLabel.Visibility = EndpointBox.Visibility = copilot ? Visibility.Collapsed : Visibility.Visible;
        KindHint.Text = kind switch
        {
            ProviderKind.GitHubCopilot =>
                "Uses your GitHub Copilot subscription through the Copilot SDK. Leave the token blank to use the GitHub account you're " +
                "signed in with (gh auth login / copilot login), or paste a GitHub token with Copilot access. " +
                "Press Test to sign in and list your Copilot models.",
            ProviderKind.GitHubModels => "GitHub Models was retired by GitHub on July 30, 2026. Use GitHub Copilot instead.",
            _ => "",
        };
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KindCombo.SelectedItem is not ProviderKind kind) return;
        UpdateKindUi(kind);
        if (_loading) return;
        EndpointBox.Text = ProviderConfig.DefaultEndpoint(kind);
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var provider = new ProviderConfig { Name = "New provider", Kind = ProviderKind.OpenAICompatible, Endpoint = ProviderConfig.DefaultEndpoint(ProviderKind.OpenAICompatible) };
        _services.Settings.Providers.Add(provider);
        _services.SaveSettings();
        _services.RaiseProvidersChanged();
        Reload(provider.Id);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        if (MessageBox.Show(this, $"Remove provider '{_current.Name}'? Agents using it will need a new provider.", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        if (!string.IsNullOrEmpty(_current.SecretName))
            _services.Secrets.Set(_current.SecretName, null);
        _services.Settings.Providers.Remove(_current);
        _services.SaveSettings();
        _services.RaiseProvidersChanged();
        Reload(null);
    }

    private bool ApplyEdits()
    {
        if (_current is null) return false;
        var kind = KindCombo.SelectedItem is ProviderKind k ? k : _current.Kind;
        if (kind != ProviderKind.GitHubCopilot && !Uri.TryCreate(EndpointBox.Text.Trim(), UriKind.Absolute, out _))
        {
            MessageBox.Show(this, "Endpoint must be an absolute URL.", Title);
            return false;
        }
        _current.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? _current.Kind.ToString() : NameBox.Text.Trim();
        _current.Kind = kind;
        _current.Endpoint = kind == ProviderKind.GitHubCopilot ? "" : EndpointBox.Text.Trim();
        if (!string.IsNullOrEmpty(KeyBox.Password))
        {
            _current.SecretName ??= "provider-" + _current.Id;
            _services.Secrets.Set(_current.SecretName, KeyBox.Password);
        }
        _services.SaveSettings();
        _services.RaiseProvidersChanged();
        return true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var id = _current?.Id;
        if (ApplyEdits()) Reload(id);
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var id = _current?.Id;
        if (!ApplyEdits() || _current is null) return;
        TestResult.Text = "Contacting provider…";
        try
        {
            var models = await _services.Models.ListModelsAsync(_current);
            TestResult.Text = models.Count == 0
                ? "Connected, but no models were reported."
                : $"Connected. {models.Count} model(s): {string.Join(", ", models.Take(20))}{(models.Count > 20 ? ", …" : "")}";
        }
        catch (Exception ex)
        {
            TestResult.Text = $"Failed: {ex.GetBaseException().Message}";
        }
        ProviderList.Items.Refresh();
        _ = id;
    }
}
