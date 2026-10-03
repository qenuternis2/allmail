using ProtonProfiles.Core.Credentials;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Network;

/// <summary>
/// Decides whether a profile's route allows opening. An unsupported Proxy configuration stays blocked and never
/// opens as System (spec §2).
/// </summary>
public static class NetworkReadinessEvaluator
{
    public static NetworkReadiness Evaluate(ProfileConfig profile, BrowserCapabilities capabilities, ICredentialStore credentials)
    {
        if (profile.WebRtcNetworkPolicy == WebRtcNetworkPolicy.RestrictNonProxiedUdpExperimental
            && !capabilities.WebRtcNetworkRestrictionSupported)
            return NetworkReadiness.UnsupportedWebRtcPolicy;
        switch (profile.NetworkMode)
        {
            case NetworkMode.Unset:
                return NetworkReadiness.NetworkModeRequired;
            case NetworkMode.System:
                return NetworkReadiness.Ready;
            case NetworkMode.Proxy:
                if (capabilities.ProxySupport == ProxySupportLevel.None) return NetworkReadiness.UnsupportedInThisBuild;
                if (profile.Proxy?.Endpoint is null) return NetworkReadiness.EndpointRequired;
                if (profile.Proxy.AuthMode == ProxyAuthMode.Basic)
                {
                    if (profile.Proxy.CredentialRef is null || !credentials.Exists(profile.Proxy.CredentialRef))
                        return NetworkReadiness.CredentialsRequired;
                }
                return NetworkReadiness.Ready;
            default:
                return NetworkReadiness.NetworkModeRequired;
        }
    }

    public static string Describe(NetworkReadiness readiness) => readiness switch
    {
        NetworkReadiness.Ready => "Готово",
        NetworkReadiness.NetworkModeRequired => "Выберите сетевой режим",
        NetworkReadiness.EndpointRequired => "Укажите адрес прокси",
        NetworkReadiness.CredentialsRequired => "Требуются учётные данные",
        NetworkReadiness.UnsupportedInThisBuild => "Прокси не поддерживается в этой сборке",
        NetworkReadiness.UnsupportedWebRtcPolicy => "Ограничение сети WebRTC недоступно в этой сборке; измените настройку или используйте экспериментальную сборку",
        _ => readiness.ToString(),
    };
}
