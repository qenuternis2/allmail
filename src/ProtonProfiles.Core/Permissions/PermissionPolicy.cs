using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Persistence;

namespace ProtonProfiles.Core.Permissions;

public enum PolicyVerdict { Allow, Deny, AskUser }

/// <summary>What the user answered in the prompt. Only the "Always" variants are persisted (spec §7).</summary>
public enum UserPermissionAnswer { AllowOnce, DenyOnce, AlwaysAllow, AlwaysDeny }

/// <summary>
/// The single authoritative permission policy keyed by (profileId, origin, kind) (spec §7, A14, A27).
/// WebView2 decisions must be made with SavesInProfile = false so the browser keeps no second store.
/// </summary>
public sealed class PermissionPolicy
{
    private readonly IProfileRepository _repository;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly Dictionary<(GenerationContext, string, PermissionKindKey), Task<bool>> _inFlight = new();

    /// <summary>Kinds that may be prompted for. Everything else is denied without a prompt.</summary>
    public static readonly IReadOnlySet<PermissionKindKey> Promptable = new HashSet<PermissionKindKey>
    {
        PermissionKindKey.Notifications, PermissionKindKey.Camera, PermissionKindKey.Microphone, PermissionKindKey.Geolocation, PermissionKindKey.ClipboardRead,
    };

    public PermissionPolicy(IProfileRepository repository, TimeProvider? time = null)
    {
        _repository = repository;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Normalizes an origin to scheme://host[:port]; returns null for anything that is not http(s).</summary>
    public static string? NormalizeOrigin(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return null;
        return u.IsDefaultPort ? $"{u.Scheme}://{u.IdnHost.ToLowerInvariant()}" : $"{u.Scheme}://{u.IdnHost.ToLowerInvariant()}:{u.Port}";
    }

    public PolicyVerdict Evaluate(Guid profileId, string? requestingUri, PermissionKindKey kind, WebRtcPagePolicy pagePolicy = WebRtcPagePolicy.Block)
    {
        if (pagePolicy == WebRtcPagePolicy.Block && kind is PermissionKindKey.Camera or PermissionKindKey.Microphone)
            return PolicyVerdict.Deny;
        var origin = NormalizeOrigin(requestingUri);
        if (origin is null || !Promptable.Contains(kind)) return PolicyVerdict.Deny;
        var stored = _repository.GetPermission(profileId, origin, kind);
        if (stored is not null) return stored.Choice == PermissionChoice.Allow ? PolicyVerdict.Allow : PolicyVerdict.Deny;
        // Never auto-grant camera, microphone or geolocation; notifications are also user-controlled.
        return PolicyVerdict.AskUser;
    }

    /// <summary>
    /// Resolves a request, deduplicating concurrent frame/top-level events for the same generation, origin and kind
    /// so the user sees one prompt (S20). <paramref name="askUser"/> runs at most once per in-flight key.
    /// </summary>
    public Task<bool> ResolveAsync(GenerationContext context, string? requestingUri, PermissionKindKey kind, Func<string, PermissionKindKey, Task<UserPermissionAnswer?>> askUser, WebRtcPagePolicy pagePolicy = WebRtcPagePolicy.Block)
    {
        var verdict = Evaluate(context.ProfileId, requestingUri, kind, pagePolicy);
        if (verdict == PolicyVerdict.Allow) return Task.FromResult(true);
        if (verdict == PolicyVerdict.Deny) return Task.FromResult(false);
        var origin = NormalizeOrigin(requestingUri)!;
        var key = (context, origin, kind);
        lock (_sync)
        {
            if (_inFlight.TryGetValue(key, out var existing)) return existing;
            var task = AskAndStoreAsync(context, origin, kind, askUser);
            // A synchronously completed prompt has already run its cleanup; caching it would replay a one-time answer.
            if (!task.IsCompleted) _inFlight[key] = task;
            return task;
        }
    }

    private async Task<bool> AskAndStoreAsync(GenerationContext context, string origin, PermissionKindKey kind, Func<string, PermissionKindKey, Task<UserPermissionAnswer?>> askUser)
    {
        try
        {
            UserPermissionAnswer? answer;
            try { answer = await askUser(origin, kind); }
            catch (Exception) { answer = null; }
            switch (answer)
            {
                case UserPermissionAnswer.AlwaysAllow:
                    _repository.SetPermission(new PermissionDecision(context.ProfileId, origin, kind, PermissionChoice.Allow, _time.GetUtcNow()));
                    return true;
                case UserPermissionAnswer.AlwaysDeny:
                    _repository.SetPermission(new PermissionDecision(context.ProfileId, origin, kind, PermissionChoice.Deny, _time.GetUtcNow()));
                    return false;
                case UserPermissionAnswer.AllowOnce:
                    return true;
                default:
                    return false; // DenyOnce, dismissed, or failed prompt
            }
        }
        finally
        {
            lock (_sync) _inFlight.Remove((context, origin, kind));
        }
    }

    public void ResetProfile(Guid profileId) => _repository.DeletePermissions(profileId);

    public static string DescribeKind(PermissionKindKey kind) => kind switch
    {
        PermissionKindKey.Notifications => "уведомления",
        PermissionKindKey.Camera => "камера",
        PermissionKindKey.Microphone => "микрофон",
        PermissionKindKey.Geolocation => "геолокация",
        PermissionKindKey.ClipboardRead => "чтение буфера обмена",
        _ => "неизвестное разрешение",
    };
}
