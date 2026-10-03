using System.Globalization;
using System.Text.Json.Serialization;

namespace ProtonProfiles.Core.Model;

/// <summary>A proxy endpoint defined only by scheme, normalized host and port (spec §6.1). Never carries credentials.</summary>
public sealed record ProxyEndpoint
{
    public ProxyType Type { get; }
    public string Host { get; }
    public int Port { get; }

    [JsonConstructor]
    private ProxyEndpoint(ProxyType type, string host, int port)
    {
        Type = type;
        Host = host;
        Port = port;
    }

    [JsonIgnore]
    public string Scheme => Type switch { ProxyType.Http => "http", _ => throw new InvalidOperationException() };

    /// <summary>Host as it must appear in a URI authority (IPv6 bracketed).</summary>
    [JsonIgnore]
    public string AuthorityHost => Host.Contains(':') ? $"[{Host}]" : Host;

    public override string ToString() => $"{Scheme}://{AuthorityHost}:{Port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Creates an endpoint from separately stored fields, normalizing and validating them.</summary>
    public static bool TryCreate(string scheme, string host, int port, out ProxyEndpoint? endpoint, out string? error)
    {
        endpoint = null;
        if (!string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            error = "Поддерживается только HTTP-прокси (CONNECT для HTTPS).";
            return false;
        }
        if (port is < 1 or > 65535)
        {
            error = "Порт должен быть целым числом от 1 до 65535.";
            return false;
        }
        if (!TryNormalizeHost(host, out var normalized))
        {
            error = "Некорректное имя узла прокси.";
            return false;
        }
        endpoint = new ProxyEndpoint(ProxyType.Http, normalized!, port);
        error = null;
        return true;
    }

    /// <summary>
    /// Parses user input such as <c>http://proxy.example:8080</c>. Rejects userinfo, path, query, fragment and anything
    /// that could smuggle extra browser arguments.
    /// </summary>
    public static bool TryParse(string? input, out ProxyEndpoint? endpoint, out string? error)
    {
        endpoint = null;
        error = "Укажите адрес в виде http://узел:порт.";
        if (string.IsNullOrWhiteSpace(input)) return false;
        var text = input.Trim();
        if (text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '\'' or ';' or ','))
        {
            error = "Адрес прокси содержит недопустимые символы.";
            return false;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || text.Contains('@'))
        {
            error = "Не указывайте логин и пароль в адресе прокси: они хранятся отдельно.";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || (uri.AbsolutePath != "/" && uri.AbsolutePath.Length > 0))
        {
            error = "Адрес прокси не должен содержать путь, параметры запроса или фрагмент.";
            return false;
        }
        if (uri.IsDefaultPort && !HasExplicitPort(text))
        {
            error = "Укажите порт прокси явно.";
            return false;
        }
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host.Trim('[', ']') : uri.Host;
        return TryCreate(uri.Scheme, host, uri.Port, out endpoint, out error);
    }

    private static bool HasExplicitPort(string text)
    {
        var afterScheme = text.IndexOf("://", StringComparison.Ordinal);
        var authority = afterScheme >= 0 ? text[(afterScheme + 3)..] : text;
        var slash = authority.IndexOf('/');
        if (slash >= 0) authority = authority[..slash];
        var close = authority.LastIndexOf(']');
        var colon = authority.LastIndexOf(':');
        return colon > close;
    }

    internal static bool TryNormalizeHost(string? host, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim().TrimEnd('.');
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (host.Length is 0 or > 253) return false;
        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            normalized = ip.ToString().ToLowerInvariant();
            return true;
        }
        try
        {
            var ascii = new IdnMapping().GetAscii(host).ToLowerInvariant();
            if (Uri.CheckHostName(ascii) != UriHostNameType.Dns) return false;
            normalized = ascii;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
