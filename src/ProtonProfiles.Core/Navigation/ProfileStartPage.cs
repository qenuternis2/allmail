using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Navigation;

/// <summary>Uses the existing persisted URL/kind fields, preserving legacy mail profiles and export privacy.</summary>
public static class ProfileStartPage
{
    public static string Url(ProfileConfig profile) => profile.TestStartUrl
        ?? (profile.Kind == ProfileKind.Mail ? NavigationPolicy.StartPage.AbsoluteUri : string.Empty);

    public static ProfileConfig WithUrl(ProfileConfig profile, string url)
    {
        var value = url.Trim();
        if (!NavigationPolicy.IsValidTestStartUrl(value))
            throw new ArgumentException("Укажите полный HTTP/HTTPS URL без логина и пароля в адресе (не более 4096 символов).", nameof(url));
        // A legacy mail profile keeps its reminders and Proton navigation when its default is unchanged.
        // A custom URL uses the existing web-profile format, whose omitted export URL blocks opening.
        return profile.Kind == ProfileKind.Mail && new Uri(value) == NavigationPolicy.StartPage
            ? profile with { TestStartUrl = null }
            : profile with { Kind = ProfileKind.Test, TestStartUrl = value };
    }
}
