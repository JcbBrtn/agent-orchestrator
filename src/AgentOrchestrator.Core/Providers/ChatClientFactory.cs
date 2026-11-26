using System.ClientModel;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentOrchestrator.Core.Models;
using AgentOrchestrator.Core.Security;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentOrchestrator.Core.Providers;

public interface IChatClientFactory
{
    IChatClient Create(ProviderConfig provider, string modelId);
}

/// <summary>
/// Ollama, LM Studio and custom providers use OpenAI-compatible chat endpoints; GitHub Copilot goes through the
/// Copilot SDK runtime.
/// </summary>
public class OpenAICompatibleChatClientFactory(ISecretStore secrets, CopilotHost? copilot = null) : IChatClientFactory
{
    public IChatClient Create(ProviderConfig provider, string modelId)
    {
        // Copilot falls back to its default model when none is chosen (built-in orchestrations ship that way).
        if (string.IsNullOrWhiteSpace(modelId) && provider.Kind != ProviderKind.GitHubCopilot)
            throw new InvalidOperationException($"No model selected for provider '{provider.Name}'.");
        if (provider.Kind == ProviderKind.GitHubModels)
            throw new InvalidOperationException(GitHubModelsRetired);
        if (provider.Kind == ProviderKind.GitHubCopilot)
        {
            if (copilot is null)
                throw new InvalidOperationException("GitHub Copilot is not available in this host.");
            return new CopilotChatClient(copilot, SecretOrNull(provider, secrets), modelId);
        }

        var key = ResolveKey(provider, secrets);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.Endpoint),
            NetworkTimeout = TimeSpan.FromMinutes(10),
        };
        return new OpenAIClient(new ApiKeyCredential(key), options).GetChatClient(modelId).AsIChatClient();
    }

    internal const string GitHubModelsRetired =
        "GitHub Models was retired by GitHub on July 30, 2026. Switch this agent to the GitHub Copilot provider.";

    internal static string? SecretOrNull(ProviderConfig provider, ISecretStore secrets) =>
        string.IsNullOrEmpty(provider.SecretName) ? null : secrets.Get(provider.SecretName) is { Length: > 0 } key ? key : null;

    internal static string ResolveKey(ProviderConfig provider, ISecretStore secrets) =>
        SecretOrNull(provider, secrets) ?? "not-needed";
}

public class ModelCatalog(ISecretStore secrets, HttpClient? http = null, CopilotHost? copilot = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<IReadOnlyList<string>> ListModelsAsync(ProviderConfig provider, CancellationToken ct = default)
    {
        if (provider.Kind == ProviderKind.GitHubModels)
            throw new InvalidOperationException(OpenAICompatibleChatClientFactory.GitHubModelsRetired);
        if (provider.Kind == ProviderKind.GitHubCopilot)
        {
            if (copilot is null)
                throw new InvalidOperationException("GitHub Copilot is not available in this host.");
            return await copilot.ListModelsAsync(OpenAICompatibleChatClientFactory.SecretOrNull(provider, secrets), ct);
        }

        var url = provider.Endpoint.TrimEnd('/') + "/models";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(provider.SecretName) && secrets.Get(provider.SecretName) is { Length: > 0 } key)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseModelIds(doc.RootElement);
    }

    /// <summary>Accepts both OpenAI format ({ "data": [{ "id" }] }) and GitHub catalog format ([{ "id" }]).</summary>
    public static IReadOnlyList<string> ParseModelIds(JsonElement root)
    {
        var array = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("data", out var data) ? data
            : root.TryGetProperty("models", out var models) ? models
            : default;
        if (array.ValueKind != JsonValueKind.Array)
            return [];

        return array.EnumerateArray()
            .Select(e => e.TryGetProperty("id", out var id) ? id.GetString()
                : e.TryGetProperty("name", out var n) ? n.GetString() : null)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
