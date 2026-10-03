using System.Text.Json;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.Core.Tests;

public class CanvasReadbackTests
{
    private static Dictionary<string, object?> Blocked(bool offscreen = true) => new()
    {
        ["htmlSupported"] = true, ["offscreenSupported"] = offscreen, ["htmlDrawing"] = true,
        ["offscreenDrawing"] = offscreen ? true : null, ["htmlGetImageData"] = "Blocked", ["htmlToDataURL"] = "Blocked",
        ["htmlToBlob"] = "Blocked", ["offscreenGetImageData"] = offscreen ? "Blocked" : "NotApplicable",
        ["offscreenConvertToBlob"] = offscreen ? "Blocked" : "NotApplicable",
    };
    private static string Cdp(object observation) => JsonSerializer.Serialize(new { result = new { type = "object", value = observation } });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Native_security_denials_confirm_local_readback_restriction(bool offscreen) =>
        Assert.Equal(GraphicsReadbackOutcome.Verified, CanvasReadback.ReadCdpResult(Cdp(Blocked(offscreen))).Outcome);

    [Theory]
    [InlineData("htmlGetImageData")]
    [InlineData("htmlToDataURL")]
    [InlineData("htmlToBlob")]
    [InlineData("offscreenGetImageData")]
    [InlineData("offscreenConvertToBlob")]
    public void Any_readable_export_blocks_startup_even_when_other_observations_are_missing(string key)
    {
        var result = CanvasReadback.ReadCdpResult(Cdp(new Dictionary<string, object?> { [key] = "Readable" }));
        Assert.Equal(GraphicsReadbackOutcome.Violation, result.Outcome);
        Assert.Contains(key, result.Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("{\"result\":{\"type\":\"object\"}}")]
    [InlineData("{\"result\":{\"value\":{}}}")]
    public void Missing_or_malformed_CDP_evidence_never_confirms_blocking(string? json) =>
        Assert.Equal(GraphicsReadbackOutcome.Unavailable, CanvasReadback.ReadCdpResult(json).Outcome);

    [Fact]
    public void Wrong_types_missing_keys_errors_and_CDP_exceptions_fail_closed()
    {
        foreach (var key in Blocked().Keys)
        {
            var missing = Blocked(); missing.Remove(key);
            Assert.Equal(GraphicsReadbackOutcome.Unavailable, CanvasReadback.ReadCdpResult(Cdp(missing)).Outcome);
            foreach (var invalid in new object?[] { null, "Unavailable", 0 })
            {
                var values = Blocked(); values[key] = invalid;
                Assert.Equal(GraphicsReadbackOutcome.Unavailable, CanvasReadback.ReadCdpResult(Cdp(values)).Outcome);
            }
        }
        var exception = JsonSerializer.Serialize(new { result = new { value = Blocked() }, exceptionDetails = new { text = "synthetic" } });
        Assert.Equal(GraphicsReadbackOutcome.Unavailable, CanvasReadback.ReadCdpResult(exception).Outcome);
        Assert.Contains("collectCanvasReadback", CanvasReadback.Script);
        Assert.Contains("await collectCanvasReadback()", CanvasReadback.EvaluationScript);
    }
}
