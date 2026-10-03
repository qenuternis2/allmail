using System.Text.RegularExpressions;

namespace ProtonProfiles.Core.Diagnostics;

/// <summary>Removes secrets and personal data from log/diagnostic text (spec §7, A16).</summary>
public static partial class Redactor
{
    [GeneratedRegex(@"(?i)\b(https?|wss?)://[^\s""'<>]+")]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?i)\b(authorization|proxy-authorization|cookie|set-cookie)\s*[:=]\s*[^\r\n]+")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|token|secret|access_token|refresh_token|session|uid)\s*[=:]\s*[^\s&;,]+")]
    private static partial Regex KeyValueRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var s = HeaderRegex().Replace(text, m => $"{m.Groups[1].Value}: [скрыто]");
        s = UrlRegex().Replace(s, m => RedactUrl(m.Value));
        s = KeyValueRegex().Replace(s, m => $"{m.Groups[1].Value}=[скрыто]");
        s = EmailRegex().Replace(s, "[адрес скрыт]");
        return s;
    }

    /// <summary>Keeps scheme, host and port; drops userinfo, path, query and fragment.</summary>
    public static string RedactUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "[url]";
        var port = u.IsDefaultPort ? string.Empty : $":{u.Port}";
        var tail = (u.AbsolutePath.Length > 1 || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) ? "/…" : "/";
        return $"{u.Scheme}://{u.Host}{port}{tail}";
    }
}
