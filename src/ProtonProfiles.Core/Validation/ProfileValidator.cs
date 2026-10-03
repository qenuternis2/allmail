using System.Text.RegularExpressions;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.Core.Validation;

/// <summary>Shared validation used by both the UI and the importer (spec §5, §9.1). Messages are Russian UI text.</summary>
public static partial class ProfileValidator
{
    public const int DisplayNameMaxLength = 100;
    public const int EmailLabelMaxLength = 254;
    public const int UserAgentMaxLength = 512;
    public const double ZoomMin = 0.5;
    public const double ZoomMax = 2.0;
    public const int ReminderMonthsMin = 1;
    public const int ReminderMonthsMax = 12;

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorRegex();

    // RFC 5646 well-formedness for the common langtag form (language[-script][-region](-variant)*). Grandfathered
    // and private-use-only tags are deliberately rejected; runtime support is checked separately on Windows.
    [GeneratedRegex("^(?<lang>[A-Za-z]{2,3}|[A-Za-z]{5,8})(-(?<script>[A-Za-z]{4}))?(-(?<region>[A-Za-z]{2}|[0-9]{3}))?(-([A-Za-z0-9]{5,8}|[0-9][A-Za-z0-9]{3}))*$")]
    private static partial Regex LanguageTagRegex();

    public static string? ValidateDisplayName(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return "Название профиля не может быть пустым.";
        if (trimmed.Length > DisplayNameMaxLength) return $"Название профиля не длиннее {DisplayNameMaxLength} символов.";
        if (trimmed.Any(char.IsControl)) return "Название профиля содержит управляющие символы.";
        return null;
    }

    public static string? ValidateEmailLabel(string? value)
    {
        if (value is null) return null;
        if (value.Length > EmailLabelMaxLength) return $"Метка адреса не длиннее {EmailLabelMaxLength} символов.";
        if (value.Any(char.IsControl)) return "Метка адреса содержит управляющие символы.";
        return null;
    }

    public static string? ValidateColor(string? value) =>
        value is not null && ColorRegex().IsMatch(value) ? null : "Цвет должен быть в формате #RRGGBB.";

    public static string? ValidateUserAgent(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "Строка User-Agent не может быть пустой в режиме «Свой».";
        if (value.Length > UserAgentMaxLength) return $"User-Agent не длиннее {UserAgentMaxLength} символов.";
        if (value.Any(char.IsControl)) return "User-Agent должен быть одной строкой без управляющих символов.";
        return null;
    }

    public static bool IsWellFormedLanguageTag(string? tag) =>
        !string.IsNullOrEmpty(tag) && tag.Length <= 35 && LanguageTagRegex().IsMatch(tag);

    public static string? ValidateLanguageTag(string? tag) =>
        IsWellFormedLanguageTag(tag) ? null : "Некорректный языковой тег BCP 47 (пример: ru-RU).";

    public static string? ValidateZoom(double value) =>
        double.IsFinite(value) && value >= ZoomMin && value <= ZoomMax ? null : "Масштаб должен быть числом от 0,5 до 2,0.";

    public static string? ValidateReminderMonths(int value) =>
        value is >= ReminderMonthsMin and <= ReminderMonthsMax ? null : "Интервал напоминания должен быть от 1 до 12 месяцев.";

    public static string? ValidatePort(int value) =>
        value is >= 1 and <= 65535 ? null : "Порт должен быть целым числом от 1 до 65535.";

    /// <summary>Validates a complete configuration before it may replace an active revision.</summary>
    public static IReadOnlyList<string> Validate(ProfileConfig p)
    {
        var errors = new List<string>();
        void Add(string? e) { if (e is not null) errors.Add(e); }

        if (p.Id == Guid.Empty) errors.Add("Профиль должен иметь идентификатор.");
        Add(ValidateDisplayName(p.DisplayName));
        Add(ValidateEmailLabel(p.EmailLabel));
        Add(ValidateColor(p.Color));
        if (!Enum.IsDefined(p.Kind)) errors.Add("Неизвестный тип профиля.");
        if (!Enum.IsDefined(p.GraphicsPolicy)) errors.Add("Неизвестная политика графики.");
        if (p.Kind != ProfileKind.Test && p.TestStartUrl is not null) errors.Add("Произвольный URL задаётся только для тестового профиля.");
        if (p.TestStartUrl is not null && !Navigation.NavigationPolicy.IsValidTestStartUrl(p.TestStartUrl))
            errors.Add("Укажите полный HTTP/HTTPS URL без логина и пароля в адресе (не более 4096 символов).");
        if (p.UserAgentMode == UserAgentMode.Custom) Add(ValidateUserAgent(p.CustomUserAgent));
        else if (p.CustomUserAgent is not null) errors.Add("В режиме User-Agent «По умолчанию» значение не задаётся.");
        if (Privacy.UserAgentHintsPrivacy.IsEnabled(p.GraphicsPolicy) && p.UserAgentMode != UserAgentMode.Default)
            errors.Add(Privacy.UserAgentHintsPrivacy.CustomUserAgentError);
        if (p.LanguageMode == LanguageMode.Custom) Add(ValidateLanguageTag(p.LanguageTag));
        else if (p.LanguageTag is not null) errors.Add("В режиме языка «Системный» тег не задаётся.");
        if (p.ScriptLocaleMode == ScriptLocaleMode.Custom) Add(ValidateLanguageTag(p.ScriptLocaleTag));
        else if (p.ScriptLocaleTag is not null) errors.Add("Тег локали скриптов задаётся только в режиме «Свой».");
        Add(Privacy.BrowserTimeZone.Validate(p.BrowserTimeZoneId));
        Add(ValidateZoom(p.ZoomFactor));
        Add(ValidateReminderMonths(p.ReminderMonths));
        if (!Enum.IsDefined(p.ColorScheme)) errors.Add("Неизвестная цветовая схема.");
        if (!Enum.IsDefined(p.TrackingPreventionLevel)) errors.Add("Неизвестный уровень защиты от отслеживания.");
        if (!Enum.IsDefined(p.WebRtcPagePolicy)) errors.Add("Неизвестный режим доступа страниц к WebRTC.");
        if (!Enum.IsDefined(p.WebRtcNetworkPolicy)) errors.Add("Неизвестный сетевой режим WebRTC.");
        if (p.NetworkMode == NetworkMode.Proxy && p.Proxy is null) errors.Add("Для режима «Прокси» нужны параметры прокси.");
        if (p.NetworkMode != NetworkMode.Proxy && p.Proxy is not null) errors.Add("Параметры прокси заданы для режима без прокси.");
        if (p.Proxy is { AuthMode: ProxyAuthMode.None, CredentialRef: not null })
            errors.Add("Ссылка на учётные данные задана для прокси без аутентификации.");
        return errors;
    }
}
