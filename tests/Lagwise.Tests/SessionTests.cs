using Lagwise.Core.Models;
using Lagwise.Core.Services;
using Lagwise.Core.Sessions;

namespace Lagwise.Tests;

public class SessionTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lagwise-session-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static List<TargetConfig> Targets() => new()
    {
        new() { Id = "gw", Name = "Gateway", Host = "192.168.0.1", IsGateway = true },
        new() { Id = "dns", Name = "DNS", Host = "x.test", Protocol = NetworkProtocol.Dns, IsDnsResolver = true },
        new() { Id = "cf", Name = "Cloudflare", Host = "1.1.1.1" }
    };

    private static Measurement M(string id, int sec, bool ok, double rtt = 10) => new()
    {
        TargetId = id, TargetName = id, Timestamp = T0.AddSeconds(sec), Success = ok, RttMs = rtt,
        ErrorMessage = ok ? null : "timeout"
    };

    private static ConnectionEvent E(string type, int sec, string target = "") =>
        new() { EventType = type, Timestamp = T0.AddSeconds(sec), TargetName = target, Description = type };

    private SessionRecorder StartedRecorder(DateTime? now = null)
    {
        var rec = new SessionRecorder(() => now ?? T0);
        rec.Start("lol", "test", Targets(), null, 1000);
        return rec;
    }

    [Fact]
    public void Targets_AreClassified_AndNeverMarkedAsGameServers()
    {
        var rec = StartedRecorder();
        var session = rec.Stop()!;

        Assert.Equal(MeasurementKind.LocalGateway, session.Targets.Single(t => t.Id == "gw").Kind);
        Assert.Equal(MeasurementKind.DnsResolution, session.Targets.Single(t => t.Id == "dns").Kind);
        Assert.Equal(MeasurementKind.ReferenceEndpoint, session.Targets.Single(t => t.Id == "cf").Kind);
        Assert.DoesNotContain(session.Targets, t => t.Kind == MeasurementKind.GameServer);
    }

    [Fact]
    public void Samples_AreRecordedWithFailuresKeptAsFailures()
    {
        var rec = StartedRecorder();
        rec.RecordMeasurement(M("cf", 1, true, 23.456));
        rec.RecordMeasurement(M("cf", 2, false));
        var session = rec.Stop()!;

        Assert.Equal(2, session.Samples.Count);
        Assert.Equal(23.46, session.Samples[0].RttMs);
        Assert.False(session.Samples[1].Ok);
        Assert.Null(session.Samples[1].RttMs);
        Assert.Equal("timeout", session.Samples[1].Error);
        Assert.True(session.IsComplete);
        Assert.NotNull(session.EndedUtc);
    }

    [Fact]
    public void NothingIsRecorded_WhenIdle()
    {
        var rec = new SessionRecorder(() => T0);
        rec.RecordMeasurement(M("cf", 1, true));
        rec.RecordEvent(E("LatencySpike", 1));
        rec.MarkLag("x");

        Assert.False(rec.IsRecording);
        Assert.Null(rec.Snapshot());
        Assert.Null(rec.Stop());
    }

    [Fact]
    public void LongSilenceBetweenCycles_IsRecordedAsMonitoringGap_NotDisconnect()
    {
        var rec = StartedRecorder();
        rec.RecordCycle(T0.AddSeconds(1));
        rec.RecordCycle(T0.AddSeconds(2));
        rec.RecordCycle(T0.AddSeconds(300)); // e.g. laptop slept
        rec.RecordCycle(T0.AddSeconds(301));
        var session = rec.Stop()!;

        var gap = Assert.Single(session.Gaps);
        Assert.Equal(T0.AddSeconds(2), gap.StartUtc);
        Assert.Equal(298, gap.DurationSeconds);
        Assert.Empty(session.Disconnects);
    }

    [Fact]
    public void NormalCycles_ProduceNoGaps()
    {
        var rec = StartedRecorder();
        for (int s = 1; s <= 60; s++) rec.RecordCycle(T0.AddSeconds(s));

        Assert.Empty(rec.Stop()!.Gaps);
    }

    [Fact]
    public void DisconnectedAndRestored_FormOneDisconnectPeriod()
    {
        var rec = StartedRecorder();
        rec.RecordEvent(E("Disconnected", 10, SessionRecorder.ConnectivityScope));
        rec.RecordEvent(E("Restored", 25, "Gateway")); // unrelated restore must not close it
        rec.RecordEvent(E("Restored", 40, SessionRecorder.ConnectivityScope));
        var session = rec.Stop()!;

        var d = Assert.Single(session.Disconnects);
        Assert.Equal(30, d.DurationSeconds);
        Assert.Equal(3, session.Events.Count);
    }

    [Fact]
    public void DisconnectStillOpenAtStop_HasNoEndTime()
    {
        var rec = StartedRecorder();
        rec.RecordEvent(E("Disconnected", 10, SessionRecorder.ConnectivityScope));
        var session = rec.Stop()!;

        Assert.Null(Assert.Single(session.Disconnects).EndUtc);
    }

    [Fact]
    public void LagMarkers_AreRecordedWithTimestamps()
    {
        var rec = StartedRecorder(T0.AddSeconds(50));
        rec.MarkLag("rubber-banding");
        rec.MarkLag(null, T0.AddSeconds(70));
        var session = rec.Stop()!;

        Assert.Equal(2, session.LagMarkers.Count);
        Assert.Equal("rubber-banding", session.LagMarkers[0].Note);
        Assert.Equal(T0.AddSeconds(70), session.LagMarkers[1].T);
    }

    [Fact]
    public void SampleCap_TruncatesAndFlagsInsteadOfGrowingForever()
    {
        var rec = new SessionRecorder(() => T0, maxSamples: 5);
        rec.Start(null, null, Targets(), null, 1000);
        for (int i = 0; i < 10; i++) rec.RecordMeasurement(M("cf", i, true));
        var session = rec.Stop()!;

        Assert.Equal(5, session.Samples.Count);
        Assert.True(session.SamplesTruncated);
    }

    [Fact]
    public void StartingTwice_IsRejected()
    {
        var rec = StartedRecorder();
        Assert.Throws<InvalidOperationException>(() => rec.Start(null, null, Targets(), null, 1000));
    }

    [Fact]
    public void Snapshot_IsIndependentAndIncomplete()
    {
        var rec = StartedRecorder();
        rec.RecordMeasurement(M("cf", 1, true));
        var snap = rec.Snapshot()!;
        rec.RecordMeasurement(M("cf", 2, true));

        Assert.Single(snap.Samples);
        Assert.False(snap.IsComplete);
        Assert.Null(snap.EndedUtc);
        Assert.Equal(2, rec.Stop()!.Samples.Count);
    }

    [Fact]
    public void Store_RoundTripsASession()
    {
        var rec = StartedRecorder();
        rec.RecordMeasurement(M("cf", 1, true, 12.5));
        rec.RecordMeasurement(M("cf", 2, false));
        rec.RecordEvent(E("Disconnected", 3, SessionRecorder.ConnectivityScope));
        rec.MarkLag("stutter", T0.AddSeconds(4));
        var session = rec.Stop()!;

        var store = new SessionStore(_dir);
        store.Save(session);
        var loaded = store.Load(session.Id)!;

        Assert.Equal("lol", loaded.GameId);
        Assert.Equal(2, loaded.Samples.Count);
        Assert.Equal(12.5, loaded.Samples[0].RttMs);
        Assert.Single(loaded.Disconnects);
        Assert.Equal("stutter", loaded.LagMarkers[0].Note);
        Assert.Equal(MeasurementKind.LocalGateway, loaded.Targets.Single(t => t.Id == "gw").Kind);
        Assert.False(File.Exists(Directory.GetFiles(_dir)[0] + ".tmp"));
    }

    [Fact]
    public void Store_ListsNewestFirst_IncludingIncompleteCheckpoints_AndSkipsCorruptFiles()
    {
        var store = new SessionStore(_dir);

        var older = new SessionRecorder(() => T0);
        older.Start(null, "older", Targets(), null, 1000);
        store.Save(older.Stop()!);

        var newer = new SessionRecorder(() => T0.AddHours(1));
        newer.Start(null, "newer", Targets(), null, 1000);
        store.Save(newer.Snapshot()!); // a checkpoint: recording never finished

        File.WriteAllText(Path.Combine(_dir, "broken.session.json"), "{nope");

        var list = store.List();
        Assert.Equal(2, list.Count);
        Assert.Equal("newer", list[0].Name);
        Assert.False(list[0].IsComplete);
        Assert.True(list[1].IsComplete);
    }

    [Fact]
    public void Store_RejectsPathTraversalIds()
    {
        var store = new SessionStore(_dir);
        Assert.Throws<ArgumentException>(() => store.Load("..\\..\\secret"));
        Assert.Throws<ArgumentException>(() => store.Delete("../x"));
    }

    [Fact]
    public void Host_ProvidesOneSharedEngineAndSavesSessionOnStop()
    {
        using var host = new LagwiseHost(_dir);

        var session = host.StartSession("lol", "host test");
        Assert.True(host.Recorder.IsRecording);
        Assert.Same(host.Engine, host.Engine);

        var stopped = host.StopSession(out var error);
        Assert.Null(error);
        Assert.Equal(session.Id, stopped!.Id);
        Assert.NotNull(host.Sessions.Load(session.Id));
        Assert.True(host.Sessions.Load(session.Id)!.IsComplete);
    }

    [Fact]
    public void Host_SettingsPersistAcrossInstances()
    {
        using (var host = new LagwiseHost(_dir))
        {
            host.Settings.IsBeginnerMode = false;
            Assert.True(host.SaveSettings(out _));
        }

        using var again = new LagwiseHost(_dir);
        Assert.False(again.Settings.IsBeginnerMode);
    }
}
