using System.Text;
using System.Text.Json;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Validation;

namespace ProtonProfiles.Core.Interchange;

public sealed record ImportError(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>A validated, not yet persisted import. Profiles carry fresh UUIDs and no browser data or trust decisions.</summary>
public sealed record ImportPreview(IReadOnlyList<ProfileConfig> Profiles, IReadOnlyList<string> Notes);

public sealed record ImportResult(ImportPreview? Preview, IReadOnlyList<ImportError> Errors)
{
    public bool Success => Preview is not null && Errors.Count == 0;
}

public sealed record ExportOptions(bool IncludeProxyEndpoints = false);

/// <summary>
/// Settings interchange format v1 (spec §9.1). Export omits UUIDs, UDF paths, credential refs, permission grants,
/// visit history, pending operations, download paths and window coordinates. Import validates the whole document
/// before anything is written and rejects unknown versions, duplicate keys, unknown fields and invalid values.
/// </summary>
public static class SettingsInterchange
{
    public const int SchemaVersion = 1;
    public const long MaxFileBytes = 1024 * 1024;
    public const int MaxRecords = 1000;

    private static readonly HashSet<string> RootKeys = ["schemaVersion", "profiles"];
    private static readonly HashSet<string> ProfileKeys =
        ["displayName", "emailLabel", "color", "isFavorite", "network", "userAgent", "language", "scriptLocale", "colorScheme", "zoomFactor", "trackingPreventionLevel", "reminderMonths"];
    private static readonly HashSet<string> NetworkKeys = ["mode", "endpoint", "authMode"];
    private static readonly HashSet<string> EndpointKeys = ["scheme", "host", "port"];
    private static readonly HashSet<string> ModeValueKeysUa = ["mode", "value"];
    private static readonly HashSet<string> ModeTagKeys = ["mode", "tag"];

    // ---------------- Export ----------------

    public static string Export(IEnumerable<ProfileConfig> profiles, ExportOptions? options = null)
    {
        options ??= new ExportOptions();
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", SchemaVersion);
            w.WriteStartArray("profiles");
            foreach (var p in profiles.OrderBy(p => p.SortOrder))
            {
                w.WriteStartObject();
                w.WriteString("displayName", p.DisplayName.Trim());
                WriteNullableString(w, "emailLabel", string.IsNullOrEmpty(p.EmailLabel) ? null : p.EmailLabel);
                w.WriteString("color", p.Color.ToUpperInvariant());
                w.WriteBoolean("isFavorite", p.IsFavorite);

                w.WriteStartObject("network");
                switch (p.NetworkMode)
                {
                    case NetworkMode.System:
                        w.WriteString("mode", "System");
                        break;
                    case NetworkMode.Unset:
                        w.WriteString("mode", "Unconfigured");
                        break;
                    case NetworkMode.Proxy:
                        w.WriteString("mode", "Proxy");
                        if (options.IncludeProxyEndpoints && p.Proxy?.Endpoint is { } ep)
                        {
                            w.WriteStartObject("endpoint");
                            w.WriteString("scheme", ep.Scheme);
                            w.WriteString("host", ep.Host);
                            w.WriteNumber("port", ep.Port);
                            w.WriteEndObject();
                        }
                        else
                        {
                            w.WriteNull("endpoint");
                        }
                        w.WriteString("authMode", (p.Proxy?.AuthMode ?? ProxyAuthMode.None).ToString());
                        break;
                }
                w.WriteEndObject();

                w.WriteStartObject("userAgent");
                w.WriteString("mode", p.UserAgentMode.ToString());
                WriteNullableString(w, "value", p.UserAgentMode == UserAgentMode.Custom ? p.CustomUserAgent : null);
                w.WriteEndObject();

                w.WriteStartObject("language");
                w.WriteString("mode", p.LanguageMode.ToString());
                WriteNullableString(w, "tag", p.LanguageMode == LanguageMode.Custom ? p.LanguageTag : null);
                w.WriteEndObject();

                w.WriteStartObject("scriptLocale");
                w.WriteString("mode", p.ScriptLocaleMode.ToString());
                WriteNullableString(w, "tag", p.ScriptLocaleMode == ScriptLocaleMode.Custom ? p.ScriptLocaleTag : null);
                w.WriteEndObject();

                w.WriteString("colorScheme", p.ColorScheme.ToString());
                w.WriteNumber("zoomFactor", p.ZoomFactor);
                w.WriteString("trackingPreventionLevel", p.TrackingPreventionLevel.ToString());
                w.WriteNumber("reminderMonths", p.ReminderMonths);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNullableString(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteString(name, value);
    }

    // ---------------- Import ----------------

    public static ImportResult Import(byte[] utf8, int startingSortOrder = 0, Func<Guid>? newId = null)
    {
        newId ??= Guid.NewGuid;
        var errors = new List<ImportError>();
        if (utf8.LongLength > MaxFileBytes)
            return Fail("$", $"Файл больше {MaxFileBytes / 1024} КиБ.");

        string text;
        try { text = new UTF8Encoding(false, true).GetString(utf8); }
        catch (DecoderFallbackException) { return Fail("$", "Файл не является корректным UTF-8."); }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowDuplicateProperties = false, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 16 });
        }
        catch (JsonException e)
        {
            return Fail("$", $"Некорректный JSON: {e.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("$", "Ожидается JSON-объект.");
            CheckKeys(root, RootKeys, "$", errors);
            if (!root.TryGetProperty("schemaVersion", out var ver) || ver.ValueKind != JsonValueKind.Number || !ver.TryGetInt32(out var v) || v != SchemaVersion)
                return Fail("$.schemaVersion", "Неподдерживаемая версия схемы.");
            if (!root.TryGetProperty("profiles", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Fail("$.profiles", "Ожидается массив profiles.");
            if (arr.GetArrayLength() > MaxRecords)
                return Fail("$.profiles", $"Не более {MaxRecords} профилей за один импорт.");

            var result = new List<ProfileConfig>();
            var notes = new List<string>();
            var i = 0;
            foreach (var item in arr.EnumerateArray())
            {
                var path = $"$.profiles[{i}]";
                var profile = ParseProfile(item, path, startingSortOrder + i, newId, errors, notes);
                if (profile is not null) result.Add(profile);
                i++;
            }
            if (errors.Count > 0) return new ImportResult(null, errors);
            return new ImportResult(new ImportPreview(result, notes), []);
        }

        static ImportResult Fail(string path, string message) => new(null, [new ImportError(path, message)]);
    }

    private static ProfileConfig? ParseProfile(JsonElement e, string path, int sortOrder, Func<Guid> newId, List<ImportError> errors, List<string> notes)
    {
        if (e.ValueKind != JsonValueKind.Object) { errors.Add(new(path, "Ожидается объект профиля.")); return null; }
        var before = errors.Count;
        CheckKeys(e, ProfileKeys, path, errors);
        foreach (var required in ProfileKeys.Where(k => k != "emailLabel"))
            if (!e.TryGetProperty(required, out _)) errors.Add(new($"{path}.{required}", "Обязательное поле отсутствует."));

        var displayName = GetString(e, "displayName", path, errors, nullable: false);
        if (displayName is not null && ProfileValidator.ValidateDisplayName(displayName) is { } dn) errors.Add(new($"{path}.displayName", dn));

        string? emailLabel = null;
        if (e.TryGetProperty("emailLabel", out _))
        {
            emailLabel = GetString(e, "emailLabel", path, errors, nullable: true);
            if (ProfileValidator.ValidateEmailLabel(emailLabel) is { } el) errors.Add(new($"{path}.emailLabel", el));
            if (emailLabel == string.Empty) emailLabel = null;
        }

        var color = GetString(e, "color", path, errors, nullable: false);
        if (color is not null && ProfileValidator.ValidateColor(color) is { } ce) errors.Add(new($"{path}.color", ce));

        var isFavorite = GetBool(e, "isFavorite", path, errors);

        // network
        var networkMode = NetworkMode.Unset;
        ProxySettings? proxy = null;
        if (GetObject(e, "network", path, errors) is { } net)
        {
            var np = $"{path}.network";
            CheckKeys(net, NetworkKeys, np, errors);
            var mode = GetString(net, "mode", np, errors, nullable: false);
            switch (mode)
            {
                case "System":
                    networkMode = NetworkMode.System;
                    if (net.TryGetProperty("endpoint", out _) || net.TryGetProperty("authMode", out _))
                        errors.Add(new(np, "Для режима System поля endpoint и authMode не допускаются."));
                    break;
                case "Unconfigured":
                    networkMode = NetworkMode.Unset;
                    if (net.TryGetProperty("endpoint", out _) || net.TryGetProperty("authMode", out _))
                        errors.Add(new(np, "Для режима Unconfigured поля endpoint и authMode не допускаются."));
                    notes.Add($"«{displayName}»: сетевой режим не задан; профиль нельзя открыть, пока он не выбран.");
                    break;
                case "Proxy":
                    networkMode = NetworkMode.Proxy;
                    var authText = GetString(net, "authMode", np, errors, nullable: false);
                    var auth = ProxyAuthMode.None;
                    if (authText is not null && !TryEnum(authText, out auth))
                        errors.Add(new($"{np}.authMode", "Неподдерживаемый режим аутентификации прокси."));
                    ProxyEndpoint? endpoint = null;
                    if (!net.TryGetProperty("endpoint", out var epEl))
                        errors.Add(new($"{np}.endpoint", "Обязательное поле отсутствует (используйте null, если адрес не экспортировался)."));
                    else if (epEl.ValueKind == JsonValueKind.Null)
                        notes.Add($"«{displayName}»: адрес прокси не экспортировался; профиль заблокирован до настройки.");
                    else if (epEl.ValueKind != JsonValueKind.Object)
                        errors.Add(new($"{np}.endpoint", "Ожидается объект или null."));
                    else
                    {
                        var ep = $"{np}.endpoint";
                        CheckKeys(epEl, EndpointKeys, ep, errors);
                        var scheme = GetString(epEl, "scheme", ep, errors, nullable: false);
                        var host = GetString(epEl, "host", ep, errors, nullable: false);
                        var port = GetInt(epEl, "port", ep, errors);
                        if (scheme is not null && host is not null && port is not null)
                        {
                            if (!ProxyEndpoint.TryCreate(scheme, host, port.Value, out endpoint, out var epError))
                                errors.Add(new(ep, epError!));
                        }
                    }
                    if (auth == ProxyAuthMode.Basic)
                        notes.Add($"«{displayName}»: учётные данные прокси не переносятся; профиль ждёт их ввода.");
                    proxy = new ProxySettings(endpoint, auth, null);
                    break;
                case null:
                    break;
                default:
                    errors.Add(new($"{np}.mode", "Допустимы значения System, Proxy, Unconfigured."));
                    break;
            }
        }

        // userAgent
        var uaMode = UserAgentMode.Default;
        string? uaValue = null;
        if (GetObject(e, "userAgent", path, errors) is { } ua)
        {
            var up = $"{path}.userAgent";
            CheckKeys(ua, ModeValueKeysUa, up, errors);
            var m = GetString(ua, "mode", up, errors, nullable: false);
            if (m is not null && !TryEnum(m, out uaMode)) errors.Add(new($"{up}.mode", "Допустимы значения Default, Custom."));
            uaValue = GetString(ua, "value", up, errors, nullable: true);
            if (uaMode == UserAgentMode.Default && uaValue is not null) errors.Add(new($"{up}.value", "В режиме Default значение должно быть null."));
            if (uaMode == UserAgentMode.Custom && ProfileValidator.ValidateUserAgent(uaValue) is { } ue) errors.Add(new($"{up}.value", ue));
        }

        // language
        var langMode = LanguageMode.System;
        string? langTag = null;
        if (GetObject(e, "language", path, errors) is { } lang)
        {
            var lp = $"{path}.language";
            CheckKeys(lang, ModeTagKeys, lp, errors);
            var m = GetString(lang, "mode", lp, errors, nullable: false);
            if (m is not null && !TryEnum(m, out langMode)) errors.Add(new($"{lp}.mode", "Допустимы значения System, Custom."));
            langTag = GetString(lang, "tag", lp, errors, nullable: true);
            if (langMode == LanguageMode.System && langTag is not null) errors.Add(new($"{lp}.tag", "В режиме System тег должен быть null."));
            if (langMode == LanguageMode.Custom && ProfileValidator.ValidateLanguageTag(langTag) is { } le) errors.Add(new($"{lp}.tag", le));
        }

        // scriptLocale
        var slMode = ScriptLocaleMode.Default;
        string? slTag = null;
        if (GetObject(e, "scriptLocale", path, errors) is { } sl)
        {
            var sp = $"{path}.scriptLocale";
            CheckKeys(sl, ModeTagKeys, sp, errors);
            var m = GetString(sl, "mode", sp, errors, nullable: false);
            if (m is not null && !TryEnum(m, out slMode)) errors.Add(new($"{sp}.mode", "Допустимы значения Default, MatchBrowserLanguage, Custom."));
            slTag = GetString(sl, "tag", sp, errors, nullable: true);
            if (slMode != ScriptLocaleMode.Custom && slTag is not null) errors.Add(new($"{sp}.tag", "Тег задаётся только в режиме Custom."));
            if (slMode == ScriptLocaleMode.Custom && ProfileValidator.ValidateLanguageTag(slTag) is { } se) errors.Add(new($"{sp}.tag", se));
        }

        var colorScheme = ColorSchemePreference.Auto;
        if (GetString(e, "colorScheme", path, errors, nullable: false) is { } cs && !TryEnum(cs, out colorScheme))
            errors.Add(new($"{path}.colorScheme", "Допустимы значения Auto, Light, Dark."));

        double zoom = 1.0;
        if (e.TryGetProperty("zoomFactor", out var z))
        {
            if (z.ValueKind != JsonValueKind.Number || !z.TryGetDouble(out zoom)) errors.Add(new($"{path}.zoomFactor", "Ожидается число."));
            else if (ProfileValidator.ValidateZoom(zoom) is { } ze) errors.Add(new($"{path}.zoomFactor", ze));
        }

        var tracking = TrackingPreventionLevel.Balanced;
        if (GetString(e, "trackingPreventionLevel", path, errors, nullable: false) is { } tp && !TryEnum(tp, out tracking))
            errors.Add(new($"{path}.trackingPreventionLevel", "Допустимы значения Balanced, Strict."));

        var reminder = GetInt(e, "reminderMonths", path, errors) ?? 6;
        if (ProfileValidator.ValidateReminderMonths(reminder) is { } re) errors.Add(new($"{path}.reminderMonths", re));

        if (errors.Count > before) return null;

        return new ProfileConfig
        {
            Id = newId(),
            DisplayName = displayName!.Trim(),
            EmailLabel = emailLabel,
            Color = color!.ToUpperInvariant(),
            SortOrder = sortOrder,
            IsFavorite = isFavorite ?? false,
            NetworkMode = networkMode,
            Proxy = proxy,
            UserAgentMode = uaMode,
            CustomUserAgent = uaValue,
            LanguageMode = langMode,
            LanguageTag = langTag,
            ScriptLocaleMode = slMode,
            ScriptLocaleTag = slTag,
            ColorScheme = colorScheme,
            ZoomFactor = zoom,
            TrackingPreventionLevel = tracking,
            ReminderMonths = reminder,
        };
    }

    private static bool TryEnum<T>(string text, out T value) where T : struct, Enum =>
        Enum.TryParse(text, ignoreCase: false, out value) && Enum.IsDefined(value) && !char.IsDigit(text[0]) && value.ToString() == text;

    private static void CheckKeys(JsonElement obj, HashSet<string> allowed, string path, List<ImportError> errors)
    {
        foreach (var p in obj.EnumerateObject())
            if (!allowed.Contains(p.Name)) errors.Add(new($"{path}.{p.Name}", "Неизвестное поле."));
    }

    private static JsonElement? GetObject(JsonElement e, string name, string path, List<ImportError> errors)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind != JsonValueKind.Object) { errors.Add(new($"{path}.{name}", "Ожидается объект.")); return null; }
        return v;
    }

    private static string? GetString(JsonElement e, string name, string path, List<ImportError> errors, bool nullable)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            if (!nullable) errors.Add(new($"{path}.{name}", "Обязательное поле отсутствует."));
            return null;
        }
        if (v.ValueKind == JsonValueKind.Null)
        {
            if (!nullable) errors.Add(new($"{path}.{name}", "Значение не может быть null."));
            return null;
        }
        if (v.ValueKind != JsonValueKind.String) { errors.Add(new($"{path}.{name}", "Ожидается строка.")); return null; }
        return v.GetString();
    }

    private static bool? GetBool(JsonElement e, string name, string path, List<ImportError> errors)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
        errors.Add(new($"{path}.{name}", "Ожидается логическое значение."));
        return null;
    }

    private static int? GetInt(JsonElement e, string name, string path, List<ImportError> errors)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && !v.GetRawText().Contains('.') && !v.GetRawText().Contains('e', StringComparison.OrdinalIgnoreCase)) return i;
        errors.Add(new($"{path}.{name}", "Ожидается целое число."));
        return null;
    }
}
