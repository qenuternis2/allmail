using System.Text.RegularExpressions;

namespace ProtonProfiles.Core.Navigation;

/// <summary>Address-bar input, without search requests or external protocol launching.</summary>
public static partial class BrowserAddress
{
    public static bool TryNormalize(string? input, out string address)
    {
        address = string.Empty;
        if (input is null || input.Length > 4096 || input.Any(char.IsControl)) return false;
        var text = input.Trim();
        if (text.Length == 0) return false;
        if (text == "about:blank") { address = text; return true; }
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (SchemePrefix().IsMatch(text) && !HostPort().IsMatch(text)) return false;
            text = "https://" + text;
        }
        if (!NavigationPolicy.IsValidTestStartUrl(text)) return false;
        address = new Uri(text).AbsoluteUri;
        return true;
    }

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]*:")]
    private static partial Regex SchemePrefix();
    [GeneratedRegex(@"^[^\s/:?#]+:\d+(?:[/?#]|$)")]
    private static partial Regex HostPort();
}
