using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Privacy;

public static class AudioPageGuard
{
    private static readonly Lazy<string> Guard = new(() => ReadResource("audio-guard.v1.js"));
    private static readonly Lazy<string> Observation = new(() => ReadResource("audio-observation.v1.js"));
    public static bool IsEnabled(GraphicsPolicy policy) => policy is GraphicsPolicy.BlockGraphicsCanvasAndWebAudioExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioAndNormalizeDprExperimental;
    public static string Script => Guard.Value;
    public static string ObservationScript => Observation.Value;
    public static string VerifyScript => "(() => {\n" + ObservationScript + "\nreturn collectAudioGuardObservation().guardVerified;\n})()";

    private static string ReadResource(string name)
    {
        using var stream = typeof(AudioPageGuard).Assembly.GetManifestResourceStream("ProtonProfiles.Core.Privacy." + name)
            ?? throw new InvalidOperationException("Web Audio resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
