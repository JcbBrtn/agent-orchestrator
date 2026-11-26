using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AgentOrchestrator.Core.Persistence;

namespace AgentOrchestrator.Core.Security;

public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string? value);
}

/// <summary>Secrets encrypted with Windows DPAPI (CurrentUser scope) and stored as base64 in a JSON file.</summary>
[SupportedOSPlatform("windows")]
public class DpapiSecretStore(string filePath) : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AgentOrchestrator.v1");
    private readonly object _gate = new();

    public string? Get(string name)
    {
        lock (_gate)
        {
            var all = Json.Load<Dictionary<string, string>>(filePath);
            if (all is null || !all.TryGetValue(name, out var protectedB64))
                return null;
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedB64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
    }

    public void Set(string name, string? value)
    {
        lock (_gate)
        {
            var all = Json.Load<Dictionary<string, string>>(filePath) ?? [];
            if (string.IsNullOrEmpty(value))
            {
                all.Remove(name);
            }
            else
            {
                var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
                all[name] = Convert.ToBase64String(bytes);
            }
            Json.Save(filePath, all);
        }
    }
}
