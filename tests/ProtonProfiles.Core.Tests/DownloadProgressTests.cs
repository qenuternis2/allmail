using ProtonProfiles.Core.Downloads;

namespace ProtonProfiles.Core.Tests;

public sealed class DownloadProgressTests
{
    [Fact]
    public void Sampling_tracks_actual_rate_and_stalls_without_inventing_speed()
    {
        var sample = new DownloadRateSampler(); sample.Reset(0, TimeSpan.Zero);
        Assert.Null(sample.Sample(250, TimeSpan.FromMilliseconds(250)));
        Assert.Equal(1000, sample.Sample(1000, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, sample.Sample(1000, TimeSpan.FromSeconds(2)));
        Assert.Equal(500, sample.Sample(2000, TimeSpan.FromSeconds(4)));
    }
    [Fact]
    public void Pause_and_automatic_restart_get_fresh_baselines()
    {
        var sample = new DownloadRateSampler(); sample.Reset(0, TimeSpan.Zero);
        Assert.Equal(1000, sample.Sample(1000, TimeSpan.FromSeconds(1)));
        sample.Reset(1000, TimeSpan.FromSeconds(10));
        Assert.Null(sample.BytesPerSecond);
        Assert.Equal(500, sample.Sample(1500, TimeSpan.FromSeconds(11)));
        Assert.Null(sample.Sample(100, TimeSpan.FromSeconds(12)));
        Assert.Equal(1000, sample.Sample(1100, TimeSpan.FromSeconds(13)));
        Assert.Null(sample.Sample(1200, TimeSpan.Zero));
    }
    [Fact]
    public void Remaining_time_requires_a_positive_rate_and_known_remaining_bytes()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), DownloadRateSampler.Remaining(1000, 4000, 1000));
        Assert.Null(DownloadRateSampler.Remaining(1000, null, 1000));
        Assert.Null(DownloadRateSampler.Remaining(1000, 4000, 0));
        Assert.Null(DownloadRateSampler.Remaining(1000, 4000, double.NaN));
        Assert.Null(DownloadRateSampler.Remaining(1000, 4000, double.PositiveInfinity));
        Assert.Null(DownloadRateSampler.Remaining(4000, 4000, 1000));
        Assert.Null(DownloadRateSampler.Remaining(0, long.MaxValue, double.Epsilon));
    }
    [Theory]
    [InlineData(1024, "1 КБ")]
    [InlineData(1536, "1,5 КБ")]
    [InlineData(1048576, "1 МБ")]
    [InlineData(0, "0 Б")]
    [InlineData(-5, "0 Б")]
    public void Sizes_are_readable_in_Russian(double bytes, string result) => Assert.Equal(result, DownloadProgress.Size(bytes));
    [Fact]
    public void Unknown_totals_and_bad_byte_counts_never_show_invalid_percent()
    {
        Assert.Equal(0, DownloadProgress.Percent(100, null));
        Assert.Equal(0, DownloadProgress.Percent(-10, 100));
        Assert.Equal(100, DownloadProgress.Percent(200, 100));
        Assert.Equal(25, DownloadProgress.Percent(1, 4));
        Assert.Equal(100, DownloadProgress.Percent(long.MaxValue, long.MaxValue));
    }
    [Theory]
    [InlineData(0.1, "1 с")]
    [InlineData(65, "1 мин 5 с")]
    [InlineData(3660, "1 ч 1 мин")]
    public void Remaining_duration_is_human_readable(double seconds, string expected) => Assert.Equal(expected, DownloadProgress.Duration(TimeSpan.FromSeconds(seconds)));
}
