namespace ProtonProfiles.Core.Privacy;

/// <summary>Application-owned early document script. Registration is distinct from Runtime coverage verification.</summary>
public static class WebRtcPageGuard
{
    public const string Version = "1";
    private static readonly Lazy<string> Source = new(() =>
    {
        using var stream = typeof(WebRtcPageGuard).Assembly.GetManifestResourceStream("ProtonProfiles.Core.Privacy.webrtc-guard.v1.js")
            ?? throw new InvalidOperationException("WebRTC guard resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Script => Source.Value;

    // Host-only readback; exposes no saved native objects or unblocking functions.
    public const string VerifyScript = """
        (() => ["RTCPeerConnection", "webkitRTCPeerConnection", "RTCIceTransport", "RTCDtlsTransport", "RTCSctpTransport"].every(name => {
          const own = Object.getOwnPropertyDescriptor(globalThis, name);
          if (!own || !("value" in own) || own.value !== undefined || own.writable || own.configurable) return false;
          for (let owner = Object.getPrototypeOf(globalThis); owner; owner = Object.getPrototypeOf(owner)) {
            const d = Object.getOwnPropertyDescriptor(owner, name);
            if (d && (!("value" in d) || d.value !== undefined || d.writable || d.configurable)) return false;
          }
          return true;
        }))()
        """;
}
