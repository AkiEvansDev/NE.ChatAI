using System.Threading;
using System.Threading.Tasks;

namespace NE.ChatAI.Plugins;

/// <summary>Read-only access to user-configured API keys stored in <c>secrets.json</c> or environment variables.</summary>
public interface IScenarioSecrets
{
    /// <summary>Reads the decrypted value of secret <paramref name="name"/>, or <see langword="null"/> when unset.</summary>
    Task<string?> GetAsync(string name, CancellationToken cancellationToken = default);
}
