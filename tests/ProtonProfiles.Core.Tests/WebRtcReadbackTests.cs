using ProtonProfiles.Core.Diagnostics;
using ProtonProfiles.Core.Lifecycle;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Privacy;

namespace ProtonProfiles.Core.Tests;

public class WebRtcReadbackTests
{
    [Theory]
    [InlineData("true", WebRtcReadbackOutcome.Verified)]
    [InlineData("false", WebRtcReadbackOutcome.Violation)]
    [InlineData("null", WebRtcReadbackOutcome.Unavailable)]
    [InlineData(null, WebRtcReadbackOutcome.Unavailable)]
    [InlineData("", WebRtcReadbackOutcome.Unavailable)]
    [InlineData("\"false\"", WebRtcReadbackOutcome.Unavailable)]
    [InlineData("{}", WebRtcReadbackOutcome.Unavailable)]
    public async Task Only_an_explicit_boolean_failure_is_a_violation(string? json, WebRtcReadbackOutcome expected)
    {
        var result = await WebRtcGuardReadback.ObserveAsync(() => Task.FromResult(json!), () => true);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Read_failure_does_not_close_a_live_profile_or_claim_verification()
    {
        using var env = new TestEnv();
        var profile = env.AddProfile();
        var lifecycle = env.Lifecycle();
        await lifecycle.OpenAsync(profile.Id);
        var session = env.Engine.Sessions[0];
        foreach (var observe in new Func<Task<string>>[]
        {
            () => Task.FromResult("null"),
            () => Task.FromException<string>(new InvalidOperationException("context no longer available")),
            () => throw new System.Runtime.InteropServices.COMException("frame read failed"),
        })
        {
            var outcome = await WebRtcGuardReadback.ObserveAsync(observe, () => lifecycle.IsCurrentGeneration(session.Context));
            Assert.Equal(WebRtcReadbackOutcome.Unavailable, outcome);
            if (outcome == WebRtcReadbackOutcome.Violation) await lifecycle.StopGenerationAsync(session.Context, "guard failure");
        }
        Assert.Equal(LifecyclePhase.Open, lifecycle.GetState(profile.Id).Phase);
        Assert.Equal(0, session.CloseCalls);
        await lifecycle.CloseAsync(profile.Id);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    [InlineData("null")]
    public async Task Navigation_during_read_discards_old_document_result(string json)
    {
        var document = new WebRtcDocumentTracker();
        document.NavigationStarting(1);
        var current = document.DocumentReady(1)!;
        var pending = new TaskCompletionSource<string>();
        var task = WebRtcGuardReadback.ObserveAsync(() => pending.Task, current);
        document.NavigationStarting(2);
        Assert.True(document.DocumentReady(2)!());
        pending.SetResult(json);
        Assert.Equal(WebRtcReadbackOutcome.Obsolete, await task);
    }

    [Fact]
    public async Task Destroyed_frame_with_pending_exception_is_obsolete()
    {
        var frame = new WebRtcDocumentTracker();
        var current = frame.DocumentReady(1)!;
        var pending = new TaskCompletionSource<string>();
        var task = WebRtcGuardReadback.ObserveAsync(() => pending.Task, current);
        frame.Destroy();
        pending.SetException(new InvalidOperationException("frame destroyed"));
        Assert.Equal(WebRtcReadbackOutcome.Obsolete, await task);
        Assert.Null(frame.DocumentReady(1));
    }

    [Fact]
    public void Stale_dom_event_does_not_reactivate_previous_document()
    {
        var document = new WebRtcDocumentTracker();
        document.NavigationStarting(1);
        var old = document.DocumentReady(1)!;
        document.NavigationStarting(2);
        Assert.Null(document.DocumentReady(1));
        Assert.False(old());
        Assert.True(document.DocumentReady(2)!());
    }

    [Fact]
    public async Task Obsolete_document_is_not_read()
    {
        var result = await WebRtcGuardReadback.ObserveAsync(() => throw new Exception("must not execute"), () => false);
        Assert.Equal(WebRtcReadbackOutcome.Obsolete, result);
    }

    [Fact]
    public async Task Explicit_false_in_current_document_still_stops_profile()
    {
        using var env = new TestEnv();
        var p = env.AddProfile();
        var lifecycle = env.Lifecycle();
        await lifecycle.OpenAsync(p.Id);
        var session = env.Engine.Sessions[0];
        var outcome = await WebRtcGuardReadback.ObserveAsync(() => Task.FromResult("false"),
            () => lifecycle.IsCurrentGeneration(session.Context));
        Assert.Equal(WebRtcReadbackOutcome.Violation, outcome);
        await lifecycle.StopGenerationAsync(session.Context, "confirmed violation");
        Assert.Equal(LifecyclePhase.Closed, lifecycle.GetState(p.Id).Phase);
        Assert.Equal("confirmed violation", lifecycle.GetState(p.Id).LastError);
    }

    [Fact]
    public async Task Readback_summary_survives_close_and_resets_for_new_generation()
    {
        using var env = new TestEnv();
        var p = env.AddProfile();
        var lifecycle = env.Lifecycle();
        await lifecycle.OpenAsync(p.Id);
        var summary = WebRtcReadbackSummary.Empty.Record(WebRtcReadbackOutcome.Unavailable, WebRtcReadbackScope.Frame);
        env.Engine.Sessions[0].WebRtcReadback = summary;
        Assert.Equal(summary, lifecycle.GetState(p.Id).WebRtcReadback);
        await lifecycle.CloseAsync(p.Id);
        Assert.Equal(summary, lifecycle.GetState(p.Id).WebRtcReadback);
        await lifecycle.OpenAsync(p.Id);
        Assert.Null(lifecycle.GetState(p.Id).WebRtcReadback);
        await lifecycle.CloseAsync(p.Id);
    }

    [Fact]
    public void Diagnostic_summary_distinguishes_unavailable_from_violations_and_coverage()
    {
        using var env = new TestEnv();
        var p = env.AddProfile();
        var summary = WebRtcReadbackSummary.Empty.Record(WebRtcReadbackOutcome.Unavailable, WebRtcReadbackScope.Frame)
            .Record(WebRtcReadbackOutcome.Obsolete, WebRtcReadbackScope.Frame);
        Assert.Equal(0, summary.Violations);
        Assert.Equal(0, summary.Verified);
        var report = DiagnosticsReport.Build(new EnvironmentInfo("test", "Core", null, null, "test", "test", "test"),
            env.Engine.Capabilities, [(p, ProfileRuntimeState.Closed(p.Id) with { WebRtcReadback = summary })], []);
        Assert.Contains("\"Unavailable\": 1", report);
        Assert.Contains("\"Obsolete\": 1", report);
        Assert.Contains("\"webRtcRuntimeCoverage\": \"NotPerformed\"", report);
        Assert.DoesNotContain(p.Id.ToString(), report);
    }
}
