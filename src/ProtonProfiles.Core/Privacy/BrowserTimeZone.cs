using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProtonProfiles.Core.Privacy;

public static partial class BrowserTimeZone
{
    [GeneratedRegex("^[A-Za-z0-9_+/-]+$")]
    private static partial Regex IdCharacters();

    public static string? Validate(string? id)
    {
        if (id is null) return null;
        if (id.Length is 0 or > 100 || !IdCharacters().IsMatch(id)
            || !TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _))
            return "Укажите часовой пояс IANA (например Europe/Berlin или UTC); пустое поле — системный.";
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return "Часовой пояс недоступен в этой системе."; }
        catch (InvalidTimeZoneException) { return "Данные часового пояса повреждены."; }
        return null;
    }

    /// <summary>Readback on an owned blank document: native Date and Intl, including winter and summer offsets.</summary>
    public static string VerificationScript(string id)
    {
        if (Validate(id) is { } error) throw new ArgumentException(error, nameof(id));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
        var dates = new[] { new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero) };
        var offsets = dates.Select(d => new { epoch = d.ToUnixTimeMilliseconds(), offset = -(int)zone.GetUtcOffset(d).TotalMinutes });
        return $$"""
            (() => {
              const expected = new Intl.DateTimeFormat('en-US', {timeZone: {{JsonSerializer.Serialize(id)}}}).resolvedOptions().timeZone;
              return Intl.DateTimeFormat().resolvedOptions().timeZone === expected
                && {{JsonSerializer.Serialize(offsets)}}.every(d => new Date(d.epoch).getTimezoneOffset() === d.offset);
            })()
            """;
    }
}
