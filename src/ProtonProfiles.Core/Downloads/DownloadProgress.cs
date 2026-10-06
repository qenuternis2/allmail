using System.Globalization;

namespace ProtonProfiles.Core.Downloads;

/// <summary>Monotonic sampling; zero speed during a stall, fresh baseline after a resume/restart.</summary>
public sealed class DownloadRateSampler
{
    private long _bytes;
    private TimeSpan _time;
    public double? BytesPerSecond { get; private set; }
    public void Reset(long bytes, TimeSpan time) { _bytes = Math.Max(0, bytes); _time = time; BytesPerSecond = null; }
    public double? Sample(long bytes, TimeSpan time)
    {
        bytes = Math.Max(0, bytes);
        if (bytes < _bytes || time < _time) { Reset(bytes, time); return null; }
        var elapsed = (time - _time).TotalSeconds;
        if (elapsed < 1) return BytesPerSecond;
        BytesPerSecond = (bytes - _bytes) / elapsed;
        _bytes = bytes; _time = time;
        return BytesPerSecond;
    }
    public static TimeSpan? Remaining(long received, long? total, double? speed)
    {
        if (total is null || total <= 0 || received >= total || speed is null || speed <= 0 || !double.IsFinite(speed.Value)) return null;
        var seconds = (total.Value - Math.Max(0, received)) / speed.Value;
        return seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null;
    }
}

public static class DownloadProgress
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    public static double Percent(long received, long? total) => total is > 0 ? Math.Clamp((double)Math.Max(0, received) / total.Value * 100, 0, 100) : 0;
    public static string Size(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes < 0) bytes = 0;
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return bytes.ToString(unit == 0 ? "0" : "0.#", Russian) + " " + units[unit];
    }
    public static string Duration(TimeSpan time) => time.TotalDays >= 1 ? $"{(int)time.TotalDays} д {time.Hours} ч"
        : time.TotalHours >= 1 ? $"{(int)time.TotalHours} ч {time.Minutes} мин"
        : time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes} мин {time.Seconds} с"
        : $"{Math.Max(1, (int)Math.Ceiling(time.TotalSeconds))} с";
}
