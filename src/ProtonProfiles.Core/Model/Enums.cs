namespace ProtonProfiles.Core.Model;

/// <summary>Persistent network mode. <see cref="Unset"/> comes from an import that omitted networking and blocks opening (spec §9.1).</summary>
public enum NetworkMode { Unset = 0, System = 1, Proxy = 2 }

/// <summary>Readiness of the configured route. Anything other than <see cref="Ready"/> blocks opening.</summary>
public enum NetworkReadiness { Ready = 0, NetworkModeRequired = 1, EndpointRequired = 2, CredentialsRequired = 3, UnsupportedInThisBuild = 4, UnsupportedWebRtcPolicy = 5, TestUrlRequired = 6, UnsupportedGraphicsPolicy = 7 }

public enum WebRtcPagePolicy { Block = 0, Allow = 1 }

public enum WebRtcNetworkPolicy { RuntimeDefault = 0, RestrictNonProxiedUdpExperimental = 1 }

public enum ProxyType { Http = 0 }

public enum ProxyAuthMode { None = 0, Basic = 1 }

public enum ProfileKind { Mail = 0, Test = 1 }

public enum GraphicsPolicy { RuntimeDefault = 0, BlockWebGlAndWebGpuExperimental = 1 }

public enum UserAgentMode { Default = 0, Custom = 1 }

public enum LanguageMode { System = 0, Custom = 1 }

public enum ScriptLocaleMode { Default = 0, MatchBrowserLanguage = 1, Custom = 2 }

/// <summary>Maps 1:1 to CoreWebView2PreferredColorScheme. UI label "Системная" is <see cref="Auto"/> (spec §5, S19).</summary>
public enum ColorSchemePreference { Auto = 0, Light = 1, Dark = 2 }

public enum TrackingPreventionLevel { Balanced = 0, Strict = 1 }

/// <summary>Main lifecycle states (spec §9). Errors, authentication and pending settings are orthogonal fields.</summary>
public enum LifecyclePhase { Closed = 0, Starting = 1, Open = 2, Closing = 3, RecoveryRequired = 4 }

public enum PendingOperationKind { Restart = 0, Reset = 1, Delete = 2, NetworkChange = 3 }

public enum PermissionKindKey { Unknown = 0, Notifications = 1, Camera = 2, Microphone = 3, Geolocation = 4, ClipboardRead = 5, Other = 99 }

public enum PermissionChoice { Allow = 0, Deny = 1 }
