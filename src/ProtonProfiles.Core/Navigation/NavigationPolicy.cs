using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Navigation;

public enum TopLevelDecision
{
    Allow,
    /// <summary>Cancel and offer "Открыть во внешнем браузере" as an explicit user action; never auto-launch.</summary>
    BlockOfferExternal,
    Block,
}

/// <summary>
/// Top-level navigation and external-link policy (spec §7). Compares parsed schemes and exact hostnames, never substrings.
/// Subresources are not filtered by this short allowlist.
/// </summary>
public sealed class NavigationPolicy
{
    public static readonly Uri StartPage = new("https://mail.proton.me/");

    /// <summary>Verified top-level hosts. Extend only after observing real authentication redirects in the prototype.</summary>
    public static readonly IReadOnlySet<string> DefaultTopLevelHosts = new HashSet<string>(StringComparer.Ordinal)
    {
        "mail.proton.me",
        "account.proton.me",
    };

    private readonly IReadOnlySet<string> _hosts;
    private readonly bool _testProfile;

    /// <summary>First page of every new generation.</summary>
    public Uri StartUri { get; }

    /// <param name="additionalVerifiedOrigins">
    /// Extra https origins (<c>https://host</c> or <c>https://host:port</c>). Production passes none; development builds use
    /// this only for the owned HTTPS test fixture.
    /// </param>
    public NavigationPolicy(IEnumerable<string>? additionalVerifiedOrigins = null, Uri? startUri = null)
    {
        var set = new HashSet<string>(DefaultTopLevelHosts, StringComparer.Ordinal);
        if (additionalVerifiedOrigins is not null)
            foreach (var o in additionalVerifiedOrigins)
            {
                if (!Uri.TryCreate(o, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
                    throw new ArgumentException("Only https origins can be added: " + o);
                set.Add(u.IsDefaultPort ? u.IdnHost.ToLowerInvariant() : $"{u.IdnHost.ToLowerInvariant()}:{u.Port}");
            }
        _hosts = set;
        StartUri = startUri ?? StartPage;
        if (EvaluateTopLevel(StartUri.AbsoluteUri) != TopLevelDecision.Allow) throw new ArgumentException("Start page must be an allowed origin.");
    }

    private NavigationPolicy(Uri testStartUri)
    {
        _hosts = DefaultTopLevelHosts;
        _testProfile = true;
        StartUri = testStartUri;
    }

    /// <summary>Each generation gets its own policy; a test profile cannot broaden any mail profile's allowlist.</summary>
    public NavigationPolicy ForProfile(ProfileConfig profile) => profile.Kind switch
    {
        ProfileKind.Mail => this,
        ProfileKind.Test when IsValidTestStartUrl(profile.TestStartUrl) => new NavigationPolicy(new Uri(profile.TestStartUrl!)),
        _ => throw new ArgumentException("Некорректный тип профиля или начальный URL профиля."),
    };

    public static bool IsValidTestStartUrl(string? url) =>
        url is { Length: > 0 and <= 4096 } && !url.Any(char.IsControl) && url == url.Trim()
        && IsExternalLaunchable(url);

    public TopLevelDecision EvaluateTopLevel(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return TopLevelDecision.Block;
        if (u.Scheme == "about" && u.AbsoluteUri == "about:blank") return TopLevelDecision.Allow;
        if (_testProfile) return IsValidTestStartUrl(uri) ? TopLevelDecision.Allow : TopLevelDecision.Block;
        if (u.Scheme != Uri.UriSchemeHttps) return IsExternalLaunchable(uri) ? TopLevelDecision.BlockOfferExternal : TopLevelDecision.Block;
        if (!string.IsNullOrEmpty(u.UserInfo)) return TopLevelDecision.Block;
        var host = u.IdnHost.ToLowerInvariant();
        if (u.IsDefaultPort ? _hosts.Contains(host) : _hosts.Contains($"{host}:{u.Port}")) return TopLevelDecision.Allow;
        return TopLevelDecision.BlockOfferExternal;
    }

    /// <summary>Only parsed http/https URLs without credentials may be passed to the external browser (spec §7, A29).</summary>
    public static bool IsExternalLaunchable(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(u.UserInfo)
        && !string.IsNullOrEmpty(u.Host);

    /// <summary>Authentication URLs can carry tokens; never open these externally on the user's behalf.</summary>
    public bool IsServiceUrl(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u)
        && (_hosts.Contains(u.IdnHost.ToLowerInvariant()) || _hosts.Contains($"{u.IdnHost.ToLowerInvariant()}:{u.Port}"));
}
