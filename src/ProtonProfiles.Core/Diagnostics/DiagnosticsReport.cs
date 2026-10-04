using System.Text.Json;
using System.Text.Json.Serialization;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Network;

namespace ProtonProfiles.Core.Diagnostics;

public enum EvidenceStatus { Pass, Fail, Blocked, NotApplicable, NotPerformed }

public sealed record CheckResult(string Id, EvidenceStatus Status, string Evidence, string? Reason);

public sealed record EnvironmentInfo(string AppVersion, string BuildFlavor, string? WebView2SdkVersion, string? WebView2RuntimeVersion, string OsDescription, string DotNetVersion, string ProcessArchitecture);

/// <summary>
/// Diagnostics export (spec F10, A16): versions, effective settings and check results. Never includes UUIDs, paths,
/// credential refs, proxy endpoints, labels, cookies, tokens or message content.
/// </summary>
public static class DiagnosticsReport
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Build(EnvironmentInfo env, BrowserCapabilities capabilities, IEnumerable<(ProfileConfig Profile, ProfileRuntimeState State)> profiles, IEnumerable<CheckResult> checks)
    {
        var index = 0;
        var payload = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            environment = env,
            capabilities,
            profiles = profiles.Select(t => new
            {
                // Ordinal alias instead of UUID or name.
                alias = $"profile-{++index}",
                phase = t.State.Phase,
                lastError = t.State.LastError is null ? null : Redactor.Redact(t.State.LastError),
                runtimeVersion = t.State.EffectiveRuntimeVersion,
                profileKind = t.Profile.Kind,
                testUrlConfigured = t.Profile.TestStartUrl is not null,
                requestedGraphicsPolicy = t.Profile.GraphicsPolicy,
                graphicsRuntimeCoverage = EvidenceStatus.NotPerformed,
                deviceScaleRestrictionRequested = Privacy.ScreenPrivacy.IsEnabled(t.Profile.GraphicsPolicy),
                deviceScaleRuntimeCoverage = EvidenceStatus.NotPerformed,
                speechSynthesisRestrictionRequested = Privacy.SpeechPrivacy.IsEnabled(t.Profile.GraphicsPolicy),
                speechSynthesisRuntimeCoverage = EvidenceStatus.NotPerformed,
                uaClientHintsRestrictionRequested = Privacy.UserAgentHintsPrivacy.IsEnabled(t.Profile.GraphicsPolicy),
                uaClientHintsRuntimeCoverage = EvidenceStatus.NotPerformed,
                localFontAccessRestrictionRequested = Privacy.FontAccessPrivacy.IsEnabled(t.Profile.GraphicsPolicy),
                localFontAccessRuntimeCoverage = EvidenceStatus.NotPerformed,
                hardwareConcurrencyNormalizationRequested = Privacy.HardwareConcurrencyPrivacy.IsEnabled(t.Profile.GraphicsPolicy),
                hardwareConcurrencyRuntimeCoverage = EvidenceStatus.NotPerformed,
                hardwareDevicesRestrictionRequested = Privacy.HardwareDevicesPrivacy.IsEnabled(t.Profile.GraphicsPolicy),
                hardwareDevicesRuntimeCoverage = EvidenceStatus.NotPerformed,
                additionalFingerprintRestrictionRequested = Privacy.AdditionalFingerprintPrivacy.IsEnabled(t.Profile.GraphicsPolicy),
                standardDocumentDefaultsRequested = Privacy.StandardFingerprintPrivacy.IsEnabled(t.Profile.GraphicsPolicy),
                clientHintHeaderRestrictionRequested = Privacy.StandardFingerprintPrivacy.IsEnabled(t.Profile.GraphicsPolicy),
                residualApiScriptRestrictionRequested = Privacy.ResidualFingerprintPrivacy.IsEnabled(t.Profile.GraphicsPolicy),
                strictProxyRoutingRequested = t.Profile.NetworkMode == NetworkMode.Proxy && Privacy.ResidualFingerprintPrivacy.IsEnabled(t.Profile.GraphicsPolicy),
                residualApiRuntimeCoverage = EvidenceStatus.NotPerformed,
                clientHintHeaderRuntimeCoverage = EvidenceStatus.NotPerformed,
                additionalFingerprintRuntimeCoverage = EvidenceStatus.NotPerformed,
                computePressureRestrictionRequested = Privacy.ComputePressurePrivacy.IsEnabled(t.Profile.GraphicsPolicy),
                computePressureRuntimeCoverage = EvidenceStatus.NotPerformed,
                canvasReadbackRuntimeCoverage = EvidenceStatus.NotPerformed,
                webAudioPageRestrictionRequested = Privacy.AudioPageGuard.IsEnabled(t.Profile.GraphicsPolicy),
                webAudioReadback = t.State.AudioReadback,
                webAudioRuntimeCoverage = EvidenceStatus.NotPerformed,
                networkMode = t.Profile.NetworkMode,
                requestedWebRtcPagePolicy = t.Profile.WebRtcPagePolicy,
                requestedWebRtcNetworkPolicy = t.Profile.WebRtcNetworkPolicy,
                webRtcReadback = t.State.WebRtcReadback,
                webRtcRuntimeCoverage = EvidenceStatus.NotPerformed,
                webRtcRouteVerification = EvidenceStatus.NotPerformed,
                independentNetworkEnforcement = false,
                proxyConfigured = t.Profile.Proxy?.Endpoint is not null,
                proxyAuth = t.Profile.Proxy?.AuthMode,
                userAgentMode = t.Profile.UserAgentMode,
                languageMode = t.Profile.LanguageMode,
                languageTag = t.Profile.LanguageTag,
                scriptLocaleMode = t.Profile.ScriptLocaleMode,
                scriptLocaleTag = t.Profile.ScriptLocaleTag,
                requestedBrowserTimeZoneId = t.Profile.BrowserTimeZoneId,
                browserTimeZoneRuntimeCoverage = EvidenceStatus.NotPerformed,
                colorScheme = t.Profile.ColorScheme,
                zoom = t.Profile.ZoomFactor,
                tracking = t.Profile.TrackingPreventionLevel,
                hasPendingRevision = t.Profile.PendingRevision is not null,
            }).ToList(),
            checks = checks.ToList(),
            excluded = "Пароли, токены, cookie, ключи, заголовки авторизации, содержимое писем, UUID, пути, адреса прокси, сетевые трассы и дампы не включаются.",
        };
        return JsonSerializer.Serialize(payload, Json);
    }
}
