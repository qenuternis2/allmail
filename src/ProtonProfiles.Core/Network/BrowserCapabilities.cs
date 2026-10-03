namespace ProtonProfiles.Core.Network;

public enum ProxySupportLevel
{
    /// <summary>Core build: Proxy profiles are blocked.</summary>
    None = 0,
    /// <summary>Undocumented browser flag; requires the experimental label (spec §2).</summary>
    ExperimentalBrowserFlag = 1,
    /// <summary>A documented, per-profile API verified by acceptance tests.</summary>
    DocumentedApi = 2,
}

/// <summary>Explicit capability description of the browser adapter (spec §9). Not a multi-engine framework.</summary>
public sealed record BrowserCapabilities(
    string EngineName,
    bool PersistentStorage,
    ProxySupportLevel ProxySupport,
    bool ProxyChangeRequiresRestart,
    bool UserAgentChangeRequiresRestart,
    bool LanguageChangeRequiresRestart,
    bool ScriptLocaleSupported,
    bool ColorSchemeLive,
    bool ZoomLive)
{
    public bool IsExperimentalNetworking => ProxySupport == ProxySupportLevel.ExperimentalBrowserFlag;
}
