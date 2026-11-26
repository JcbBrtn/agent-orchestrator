using GitHub.Copilot;

namespace AgentOrchestrator.Core.Providers;

/// <summary>
/// Owns the shared GitHub Copilot runtime (one child process for the whole app). It is started on first use and
/// authenticates as the signed-in GitHub user (gh / copilot login) unless a token is supplied.
/// </summary>
public sealed class CopilotHost(string dataDirectory) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CopilotClient? _client;
    private string? _clientToken;

    public async Task<CopilotClient> GetClientAsync(string? token, CancellationToken ct = default)
    {
        token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        await _gate.WaitAsync(ct);
        try
        {
            if (_client is not null && _clientToken == token)
                return _client;
            if (_client is not null)
                await _client.DisposeAsync();
            _client = null;

            Directory.CreateDirectory(dataDirectory);
            var options = new CopilotClientOptions
            {
                Mode = CopilotClientMode.Empty,
                BaseDirectory = dataDirectory,
                GitHubToken = token,
            };
            // Pin the runtime bundled with the SDK; an inherited COPILOT_CLI_PATH may point at an incompatible CLI.
            if (FindBundledRuntime() is { } runtime)
                options.Connection = RuntimeConnection.ForStdio(runtime, null);

            var client = new CopilotClient(options);
            try
            {
                await client.StartAsync(ct);
                var auth = await client.GetAuthStatusAsync(ct);
                if (!auth.IsAuthenticated)
                    throw new InvalidOperationException(
                        "Not signed in to GitHub Copilot. Sign in with `gh auth login` (or `copilot login`), " +
                        "or paste a GitHub token with Copilot access in Providers…" +
                        (string.IsNullOrEmpty(auth.StatusMessage) ? "" : $" ({auth.StatusMessage})"));
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }

            _client = client;
            _clientToken = token;
            return client;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(string? token, CancellationToken ct = default)
    {
        var client = await GetClientAsync(token, ct);
        var models = await client.ListModelsAsync(ct);
        return models
            .Where(m => !string.Equals(m.Policy?.State, "disabled", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FindBundledRuntime()
    {
        var runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");
        if (!Directory.Exists(runtimes))
            return null;
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var exe = OperatingSystem.IsWindows() ? "copilot-runtime.exe" : "copilot-runtime";
        return Directory.GetDirectories(runtimes)
            .OrderByDescending(d => Path.GetFileName(d).EndsWith("-" + arch, StringComparison.OrdinalIgnoreCase))
            .Select(d => Path.Combine(d, "native", exe))
            .FirstOrDefault(File.Exists);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        _client = null;
    }
}
