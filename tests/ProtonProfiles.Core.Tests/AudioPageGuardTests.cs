using System.Text.Json;
using ProtonProfiles.Core.Diagnostics;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.Core.Tests;

public class AudioPageGuardTests
{
    [Fact]
    public void Opt_in_mode_loads_guard_and_observer_from_the_shipped_assembly()
    {
        foreach (var policy in Enum.GetValues<GraphicsPolicy>())
            Assert.Equal(policy is GraphicsPolicy.BlockGraphicsCanvasAndWebAudioExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioAndNormalizeDprExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioDprAndSpeechSynthesisExperimental or GraphicsPolicy.BlockGraphicsCanvasAudioDprSpeechAndUaHintsExperimental, AudioPageGuard.IsEnabled(policy));
        Assert.Contains("OfflineAudioContext", AudioPageGuard.Script);
        Assert.Contains(AudioPageGuard.ObservationScript, AudioPageGuard.VerifyScript);
    }

    [Theory]
    [InlineData("true", PageGuardReadbackOutcome.Verified)]
    [InlineData("false", PageGuardReadbackOutcome.Violation)]
    [InlineData("null", PageGuardReadbackOutcome.Unavailable)]
    [InlineData("\"false\"", PageGuardReadbackOutcome.Unavailable)]
    [InlineData("{}", PageGuardReadbackOutcome.Unavailable)]
    public async Task Only_explicit_current_document_booleans_are_actionable(string json, PageGuardReadbackOutcome outcome) =>
        Assert.Equal(outcome, await PageGuardReadback.ObserveAsync(() => Task.FromResult(json), () => true));

    [Fact]
    public async Task Unavailable_and_obsolete_observations_do_not_become_violations()
    {
        Assert.Equal(PageGuardReadbackOutcome.Unavailable, await PageGuardReadback.ObserveAsync(
            () => throw new InvalidOperationException("frame destroyed"), () => true));
        Assert.Equal(PageGuardReadbackOutcome.Obsolete, await PageGuardReadback.ObserveAsync(
            () => throw new Exception("must not execute"), () => false));
        var tracker = new PageDocumentTracker();
        tracker.NavigationStarting(1);
        var current = tracker.DocumentReady(1)!;
        var pending = new TaskCompletionSource<string>();
        var task = PageGuardReadback.ObserveAsync(() => pending.Task, current);
        tracker.NavigationStarting(2);
        pending.SetResult("false");
        Assert.Equal(PageGuardReadbackOutcome.Obsolete, await task);
    }

    [Fact]
    public async Task Observations_survive_close_but_do_not_leak_into_the_next_generation_or_claim_full_coverage()
    {
        using var env = new TestEnv();
        var p = env.AddProfile(change: p => p with { GraphicsPolicy = GraphicsPolicy.BlockGraphicsCanvasAndWebAudioExperimental });
        env.Engine.Capabilities = env.Engine.Capabilities with { GraphicsRestrictionSupported = true };
        var lifecycle = env.Lifecycle();
        await lifecycle.OpenAsync(p.Id);
        var summary = PageGuardReadbackSummary.Empty.Record(PageGuardReadbackOutcome.Verified, "MainDocument")
            .Record(PageGuardReadbackOutcome.Unavailable, "Frame");
        env.Engine.Sessions[0].AudioReadback = summary;
        Assert.Equal(summary, lifecycle.GetState(p.Id).AudioReadback);
        await lifecycle.CloseAsync(p.Id);
        Assert.Equal(summary, lifecycle.GetState(p.Id).AudioReadback);
        var text = DiagnosticsReport.Build(new EnvironmentInfo("test", "test", null, null, "test", "test", "test"),
            env.Engine.Capabilities, [(p, lifecycle.GetState(p.Id))], []);
        using var report = JsonDocument.Parse(text);
        var profile = report.RootElement.GetProperty("profiles")[0];
        Assert.True(profile.GetProperty("webAudioPageRestrictionRequested").GetBoolean());
        Assert.Equal("NotPerformed", profile.GetProperty("webAudioRuntimeCoverage").GetString());
        await lifecycle.OpenAsync(p.Id);
        Assert.Null(lifecycle.GetState(p.Id).AudioReadback);
        await lifecycle.CloseAsync(p.Id);
    }
}
