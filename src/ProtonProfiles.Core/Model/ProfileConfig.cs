namespace ProtonProfiles.Core.Model;

/// <summary>
/// Persistent profile configuration (spec §9). Runtime state (phase, process id, active revision, last error)
/// lives in <see cref="Lifecycle.ProfileRuntimeState"/> and is never stored here.
/// </summary>
public sealed record ProfileConfig
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; init; }
    public ProfileKind Kind { get; init; } = ProfileKind.Mail;
    public string? TestStartUrl { get; init; }
    public GraphicsPolicy GraphicsPolicy { get; init; } = GraphicsPolicy.RuntimeDefault;

    public PrivacyException PrivacyExceptions { get; init; } = PrivacyException.None;

    public string? EmailLabel { get; init; }
    public string Color { get; init; } = "#2563EB";
    public int SortOrder { get; init; }
    public bool IsFavorite { get; init; }
    public bool IsPinned { get; init; }

    public long ConfigRevision { get; init; } = 1;
    public long? LastAppliedRevision { get; init; }
    public long? PendingRevision { get; init; }

    public NetworkMode NetworkMode { get; init; } = NetworkMode.System;
    public ProxySettings? Proxy { get; init; }

    public WebRtcPagePolicy WebRtcPagePolicy { get; init; } = WebRtcPagePolicy.Block;
    public WebRtcNetworkPolicy WebRtcNetworkPolicy { get; init; } = WebRtcNetworkPolicy.RuntimeDefault;

    public UserAgentMode UserAgentMode { get; init; } = UserAgentMode.Default;
    public string? CustomUserAgent { get; init; }

    public LanguageMode LanguageMode { get; init; } = LanguageMode.System;
    public string? LanguageTag { get; init; }

    public ScriptLocaleMode ScriptLocaleMode { get; init; } = ScriptLocaleMode.Default;
    public string? ScriptLocaleTag { get; init; }

    /// <summary>IANA browser time zone; null keeps the host time zone. Independent of reminder dates.</summary>
    public string? BrowserTimeZoneId { get; init; }
    public bool BrowserTimeZoneAuto { get; init; }

    public ColorSchemePreference ColorScheme { get; init; } = ColorSchemePreference.Auto;
    public double ZoomFactor { get; init; } = 1.0;
    public WindowBounds? WindowBounds { get; init; }
    public TrackingPreventionLevel TrackingPreventionLevel { get; init; } = TrackingPreventionLevel.Balanced;
    /// <summary>WebView2 SmartScreen for sites and downloads in this profile's user data folder.</summary>
    public bool ReputationCheckingEnabled { get; init; } = true;
    public string? DownloadDirectory { get; init; }

    public DateTimeOffset? LastOpenedAt { get; init; }
    public DateTimeOffset? LastUserConfirmedVisitAt { get; init; }
    public DateOnly? ConfirmationLocalDate { get; init; }
    public string? ConfirmationTimeZoneId { get; init; }
    public int ReminderMonths { get; init; } = 6;
    public DateTimeOffset? SnoozedUntil { get; init; }

    /// <summary>Settings that cannot change on a live environment (spec §5, §9).</summary>
    public static bool RequiresRestart(ProfileConfig before, ProfileConfig after) =>
        before.Kind != after.Kind
        || !string.Equals(before.TestStartUrl, after.TestStartUrl, StringComparison.Ordinal)
        || before.GraphicsPolicy != after.GraphicsPolicy
        || before.PrivacyExceptions != after.PrivacyExceptions
        || ((Privacy.ScreenPrivacy.IsEnabled(before.GraphicsPolicy) || Privacy.ScreenPrivacy.IsEnabled(after.GraphicsPolicy)) && before.ZoomFactor != after.ZoomFactor)
        || before.NetworkMode != after.NetworkMode
        || before.WebRtcPagePolicy != after.WebRtcPagePolicy
        || before.WebRtcNetworkPolicy != after.WebRtcNetworkPolicy
        || before.Proxy != after.Proxy
        || before.UserAgentMode != after.UserAgentMode
        || !string.Equals(before.CustomUserAgent, after.CustomUserAgent, StringComparison.Ordinal)
        || before.LanguageMode != after.LanguageMode
        || !string.Equals(before.LanguageTag, after.LanguageTag, StringComparison.Ordinal)
        || before.ScriptLocaleMode != after.ScriptLocaleMode
        || !string.Equals(before.ScriptLocaleTag, after.ScriptLocaleTag, StringComparison.Ordinal)
        || before.BrowserTimeZoneAuto != after.BrowserTimeZoneAuto
        || !string.Equals(before.BrowserTimeZoneId, after.BrowserTimeZoneId, StringComparison.Ordinal)
        || before.TrackingPreventionLevel != after.TrackingPreventionLevel
        || before.ReputationCheckingEnabled != after.ReputationCheckingEnabled;

    /// <summary>The effective script locale tag that must be applied to every controller of this profile, or null for the Runtime default.</summary>
    public string? ResolveScriptLocale(string systemUiLanguage) => ScriptLocaleMode switch
    {
        ScriptLocaleMode.Default => null,
        ScriptLocaleMode.MatchBrowserLanguage => LanguageMode == LanguageMode.Custom ? LanguageTag : systemUiLanguage,
        ScriptLocaleMode.Custom => ScriptLocaleTag,
        _ => null,
    };
}

public sealed record ProxySettings(ProxyEndpoint? Endpoint, ProxyAuthMode AuthMode, string? CredentialRef);

public sealed record WindowBounds(double Left, double Top, double Width, double Height, bool Maximized);
