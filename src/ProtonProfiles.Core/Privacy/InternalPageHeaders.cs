namespace ProtonProfiles.Core.Privacy;

/// <summary>Recognizes reserved local application origins, never arbitrary URL substrings.</summary>
public static class InternalPageHeaders
{
    public static bool IsInternalUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        return uri.Host.Equals("diagnostics.invalid", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("protonprofiles.invalid", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".protonprofiles.invalid", StringComparison.OrdinalIgnoreCase);
    }
}
