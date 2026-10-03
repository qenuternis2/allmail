namespace ProtonProfiles.Core.Credentials;

public sealed record ProxyCredential(string UserName, string Password);

/// <summary>
/// Proxy secrets live in Windows Credential Manager or DPAPI CurrentUser, never in SQLite, URIs, arguments or logs (spec §6.1).
/// </summary>
public interface ICredentialStore
{
    bool Exists(string credentialRef);
    ProxyCredential? Read(string credentialRef);
    /// <summary>Writes a secret and returns a fresh reference. A changed secret always gets a new reference so older generations cannot read it.</summary>
    string Write(Guid profileId, ProxyCredential credential);
    void Delete(string credentialRef);
    void DeleteAllForProfile(Guid profileId);
}

/// <summary>Test/fixture store. Not used by the shipped application.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, ProxyCredential> _items = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public bool Exists(string credentialRef) { lock (_sync) return _items.ContainsKey(credentialRef); }

    public ProxyCredential? Read(string credentialRef) { lock (_sync) return _items.GetValueOrDefault(credentialRef); }

    public string Write(Guid profileId, ProxyCredential credential)
    {
        var reference = CredentialRefs.Create(profileId);
        lock (_sync) _items[reference] = credential;
        return reference;
    }

    public void Delete(string credentialRef) { lock (_sync) _items.Remove(credentialRef); }

    public void DeleteAllForProfile(Guid profileId)
    {
        var prefix = CredentialRefs.Prefix(profileId);
        lock (_sync)
            foreach (var key in _items.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                _items.Remove(key);
    }
}

public static class CredentialRefs
{
    public const string Root = "ProtonProfiles/proxy/";
    public static string Prefix(Guid profileId) => $"{Root}{profileId:D}/";
    public static string Create(Guid profileId) => $"{Prefix(profileId)}{Guid.NewGuid():N}";
}
