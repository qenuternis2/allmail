using System.Text.Json;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.Core.Tests;

public class GraphicsRestrictionTests
{
    private static Dictionary<string, object?> Blocked(bool offscreen = true) => new()
    {
        ["canvasWebGl"] = false, ["canvasExperimentalWebGl"] = false, ["canvasWebGl2"] = false,
        ["offscreenSupported"] = offscreen, ["offscreenWebGl"] = offscreen ? false : null,
        ["offscreenWebGl2"] = offscreen ? false : null,
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Explicit_unavailable_contexts_are_verified(bool offscreen) =>
        Assert.Equal(GraphicsReadbackOutcome.Verified, GraphicsRestriction.ReadWebGlResult(JsonSerializer.Serialize(Blocked(offscreen))).Outcome);

    [Theory]
    [InlineData("canvasWebGl")]
    [InlineData("canvasExperimentalWebGl")]
    [InlineData("canvasWebGl2")]
    [InlineData("offscreenWebGl")]
    [InlineData("offscreenWebGl2")]
    public void Any_live_context_blocks_even_when_other_checks_are_missing(string key)
    {
        var result = GraphicsRestriction.ReadWebGlResult(JsonSerializer.Serialize(new Dictionary<string, object?> { [key] = true }));
        Assert.Equal(GraphicsReadbackOutcome.Violation, result.Outcome);
        Assert.Contains(key.StartsWith("offscreen", StringComparison.Ordinal) ? "OffscreenCanvas" : "Canvas", result.Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{broken")]
    [InlineData("{}")]
    public void Missing_or_invalid_results_never_confirm_blocking(string? json) =>
        Assert.Equal(GraphicsReadbackOutcome.Unavailable, GraphicsRestriction.ReadWebGlResult(json).Outcome);

    [Fact]
    public void Null_missing_and_wrongly_typed_checks_never_confirm_blocking()
    {
        foreach (var key in Blocked().Keys)
        {
            foreach (var invalid in new object?[] { null, "false", 0 })
            {
                var values = Blocked(); values[key] = invalid;
                Assert.Equal(GraphicsReadbackOutcome.Unavailable, GraphicsRestriction.ReadWebGlResult(JsonSerializer.Serialize(values)).Outcome);
            }
            var missing = Blocked(); missing.Remove(key);
            Assert.Equal(GraphicsReadbackOutcome.Unavailable, GraphicsRestriction.ReadWebGlResult(JsonSerializer.Serialize(missing)).Outcome);
        }
    }
}
