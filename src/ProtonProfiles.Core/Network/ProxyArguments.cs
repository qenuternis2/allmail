using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Network;

/// <summary>
/// Builds the experimental <c>--proxy-server</c> browser flag strictly from parsed fields (spec §6.2).
/// Microsoft advises against browser flags in production [S6]; callers must label this path experimental.
/// </summary>
public static class ProxyArguments
{
    public static string BuildProxyServerFlag(ProxyEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var value = endpoint.ToString();
        // Defence in depth: the endpoint type already guarantees this, but nothing user-controlled may add flags.
        if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '\'' or ';' or ','))
            throw new ArgumentException("Proxy endpoint contains characters that could alter browser arguments.");
        // Do not let the Runtime bypass the proxy for anything except loopback, which Chromium bypasses by default.
        return $"--proxy-server={value}";
    }
}

/// <summary>
/// Complete arguments, assigned once before environment creation. The experimental IP-handling flag restricts
/// non-proxied UDP; it does not disable WebRTC, prevent TCP traffic, or prove route enforcement.
/// </summary>
public static class BrowserArguments
{
    public const string WebRtcPolicyFlag = "--force-webrtc-ip-handling-policy=disable_non_proxied_udp";

    // --disable-webgl sets document preferences, but OffscreenCanvas ignores those preferences.
    // Disable GPU access AND its software 3D fallback to cover native offscreen/worker contexts.
    // CPU Canvas 2D remains available; this opt-in mode also disables accelerated compositing/video.
    public const string GraphicsPolicyFlags = "--disable-webgl --disable-gpu --disable-software-rasterizer --disable-features=WebGPU,WebGPUService";
    public const string DisplayScaleFlag = "--force-device-scale-factor=1";
    public const string CanvasReadbackFlag = "--disable-reading-from-canvas";

    public static string Build(ProxyEndpoint? proxy, WebRtcNetworkPolicy policy = WebRtcNetworkPolicy.RuntimeDefault, GraphicsPolicy graphics = GraphicsPolicy.RuntimeDefault)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (!Enum.IsDefined(graphics)) throw new ArgumentOutOfRangeException(nameof(graphics));
        var arguments = new List<string>();
        if (graphics != GraphicsPolicy.RuntimeDefault) arguments.Add(GraphicsPolicyFlags);
        if (graphics is GraphicsPolicy.BlockWebGlWebGpuAndCanvasReadbackExperimental or GraphicsPolicy.BlockGraphicsCanvasAndWebAudioExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioAndNormalizeDprExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioDprAndSpeechSynthesisExperimental) arguments.Add(CanvasReadbackFlag);
        if (Privacy.ScreenPrivacy.IsEnabled(graphics)) arguments.Add(DisplayScaleFlag);
        if (Privacy.SpeechPrivacy.IsEnabled(graphics)) arguments.Add(Privacy.SpeechPrivacy.BrowserFlag);
        if (policy == WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental) arguments.Add(WebRtcPolicyFlag);
        if (proxy is not null) arguments.Add(ProxyArguments.BuildProxyServerFlag(proxy));
        return string.Join(" ", arguments);
    }
}

/// <summary>
/// Decides whether proxy credentials may be released to a BasicAuthenticationRequested challenge (spec §6.2).
/// The event also fires for website 401 responses; credentials go only to an exact match of this generation's proxy.
/// </summary>
public static class ProxyChallengeMatcher
{
    public enum Decision { ReleaseProxyCredentials, CancelMismatch, NotAProxyChallenge }

    /// <param name="challengeUri">The <c>Uri</c> reported by the event; for proxy challenges it identifies the proxy [S14].</param>
    /// <param name="isProxyChallenge">Whether the engine reported the challenge as coming from a proxy (407 context).</param>
    public static Decision Evaluate(ProxyEndpoint? configured, string? challengeUri, bool isProxyChallenge)
    {
        if (!isProxyChallenge) return Decision.NotAProxyChallenge;
        if (configured is null || string.IsNullOrEmpty(challengeUri)) return Decision.CancelMismatch;
        if (!Uri.TryCreate(challengeUri, UriKind.Absolute, out var uri)) return Decision.CancelMismatch;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return Decision.CancelMismatch;
        if (!string.Equals(uri.Scheme, configured.Scheme, StringComparison.OrdinalIgnoreCase)) return Decision.CancelMismatch;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host.Trim('[', ']') : uri.Host;
        if (!ProxyEndpoint.TryNormalizeHost(host, out var normalized) || normalized != configured.Host) return Decision.CancelMismatch;
        if (uri.Port != configured.Port) return Decision.CancelMismatch;
        return Decision.ReleaseProxyCredentials;
    }
}

/// <summary>Allows one bounded retry per generation, then requires user action (spec §6.1).</summary>
public sealed class ProxyAuthRetryBudget
{
    private readonly int _maxAttempts;
    private int _attempts;

    public ProxyAuthRetryBudget(int maxAttempts = 2) => _maxAttempts = maxAttempts;

    /// <summary>Returns true if credentials may be supplied for another attempt.</summary>
    public bool TryConsume() => Interlocked.Increment(ref _attempts) <= _maxAttempts;

    public bool Exhausted => Volatile.Read(ref _attempts) >= _maxAttempts;
}
