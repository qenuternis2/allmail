namespace ProtonProfiles.Core.Privacy;

public static class GraphicsRestriction
{
    // Only an owned blank document is used for this synchronous startup check.
    // WebGPU requires a secure origin and is observed separately on the HTTPS probe.
    public const string WebGlVerificationScript = """
        (() => {
          for (const type of ['webgl', 'experimental-webgl', 'webgl2']) {
            if (document.createElement('canvas').getContext(type)) return false;
          }
          for (const type of ['webgl', 'webgl2']) {
            if (typeof OffscreenCanvas === 'function' && new OffscreenCanvas(1, 1).getContext(type)) return false;
          }
          return true;
        })()
        """;
}
