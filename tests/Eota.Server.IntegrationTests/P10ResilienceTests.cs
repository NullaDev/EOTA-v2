using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Eota.Client.Core;
using Eota.Client.Transport.InProcess;
using Eota.Kernel.Matches;
using Eota.Server.Application;
using Eota.Server.Infrastructure;
using Eota.Transport.Contracts;

namespace Eota.Server.IntegrationTests;

public sealed class P10ResilienceTests
{
    [Theory]
    [InlineData("foreign-seat")]
    [InlineData("hash")]
    [InlineData("order")]
    [InlineData("capacity")]
    public void InvalidResumeBatchCannotPartiallyPublishFrames(string mutation)
    {
        var state = MatchFactory.Create(Fixture.Load()).State!;
        var view = ObserverProjector.Project(state, Audience.PlayerOne); var hash = ObserverViewHasher.Compute(view);
        var store = new ObserverStore("resume", 2); Assert.True(store.Apply(new ServerEnvelope(0, "resume", 1, 0, new ObserverSnapshotPayload(null, view, hash))));
        var frame = new PresentationFramePayload(1, view, hash, []);
        var foreign = ObserverProjector.Project(state, Audience.PlayerTwo);
        var payload = mutation switch
        {
            "foreign-seat" => new ObserverResumePayload("r", view, hash, [frame with { View = foreign, ObserverViewHash = ObserverViewHasher.Compute(foreign) }], view, hash),
            "hash" => new ObserverResumePayload("r", view, hash, [frame with { ObserverViewHash = new string('0', 64) }], view, hash),
            "order" => new ObserverResumePayload("r", view, hash, [frame, frame], view, hash),
            _ => new ObserverResumePayload("r", view, hash, [frame, frame with { FrameId = 2 }, frame with { FrameId = 3 }], view, hash)
        };
        Assert.False(store.Apply(new ServerEnvelope(0, "resume", 2, 0, payload)));
        Assert.Equal(hash, store.ObserverViewHash); Assert.Null(store.TakeResumedPresentation()); Assert.Empty(store.DrainPresentationFrames());
        Assert.Equal("invalid-resume-batch", Assert.Single(store.CaptureFailures()).Code);
    }

    [Theory]
    [InlineData(Audience.PlayerOne)]
    [InlineData(Audience.PlayerTwo)]
    [InlineData(Audience.Spectator)]
    public async Task ReconnectReceivesOnlyItsOwnMissingFrames(Audience audience)
    {
        await using var actor = new MatchActor("resume", Fixture.Load());
        await using var one = new GameClient(await InProcessGameTransport.ConnectAsync(actor, Audience.PlayerOne));
        await using var two = new GameClient(await InProcessGameTransport.ConnectAsync(actor, Audience.PlayerTwo));
        ObserverResumeCursor cursor;
        await using (var old = new GameClient(await InProcessGameTransport.ConnectAsync(actor, audience)))
        { await old.SynchronizeAsync(); cursor = new(old.Store.MatchRevision, old.Store.ObserverViewHash!); }
        await Send(one, new PlanCardPayload(1, 0)); await Send(one, new SubmitTurnPayload()); await Send(two, new SubmitTurnPayload());
        await using var client = new GameClient(await InProcessGameTransport.ConnectAsync(actor, audience));
        await client.SynchronizeAsync(); var expected = client.Store.ObserverViewHash;
        await client.ResumeAsync(cursor); var replay = Assert.IsType<ResumedPresentation>(client.Store.TakeResumedPresentation());
        Assert.NotEmpty(replay.Frames); Assert.Equal(expected, client.Store.ObserverViewHash);
        Assert.All(replay.Frames, frame => Assert.Equal(audience, frame.View.Audience));
        if (audience == Audience.Spectator) { Assert.Null(replay.BaseView.Private); Assert.All(replay.Frames, f => Assert.Null(f.View.Private)); }
        else { Assert.All(replay.Frames, f => Assert.Equal(audience == Audience.PlayerOne ? 0 : 1, f.View.Private!.PlayerId)); }
        Assert.Equal(1, (await actor.GetOperationsAsync()).ResumeHits);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("bytes")]
    [InlineData("foreign-seat")]
    [InlineData("restart")]
    public async Task MissingOrForeignCursorFallsBackToFullSnapshot(string reason)
    {
        await using var actor = new MatchActor("resume", Fixture.Load(), new MatchActorOptions(ReplayCapacity: reason == "count" ? 2 : 128,
            ReplayBytesPerAudience: reason == "bytes" ? 1 : 1048576));
        await using var one = new GameClient(await InProcessGameTransport.ConnectAsync(actor, Audience.PlayerOne));
        await one.SynchronizeAsync(); var cursor = new ObserverResumeCursor(one.Store.MatchRevision, one.Store.ObserverViewHash!);
        await Send(one, new PlanCardPayload(1, 0)); await Send(one, new CancelPlanPayload(1));
        await using var replacement = reason == "restart" ? new MatchActor("resume", Fixture.Load()) : null;
        if (reason == "restart") { cursor = new(one.Store.MatchRevision, one.Store.ObserverViewHash!); }
        await using var client = new GameClient(await InProcessGameTransport.ConnectAsync(replacement ?? actor,
            reason == "foreign-seat" ? Audience.PlayerTwo : Audience.PlayerOne));
        await client.SynchronizeAsync(); var expected = client.Store.ObserverViewHash;
        await client.ResumeAsync(cursor); Assert.Null(client.Store.TakeResumedPresentation()); Assert.Equal(expected, client.Store.ObserverViewHash);
        Assert.False(client.Store.NeedsSnapshot);
    }

    [Fact]
    public async Task CompactionAndTailPreserveHashAndBoundRetryWindow()
    {
        var directory = NewDirectory(); var request = Fixture.Load(); var options = new FileMatchJournalOptions(16, 4);
        var retry = new ClientEnvelope(0, "rolling", "first-plan", 1, 0, new PlanCardPayload(1, 0));
        try
        {
            MatchDiagnostics expected;
            await using (var actor = new MatchActor("rolling", request, new MatchActorOptions(MaximumCommandKeys: 4), new FileMatchJournal(directory, request, options)))
            {
                await using var one = new GameClient(await InProcessGameTransport.ConnectAsync(actor, Audience.PlayerOne));
                for (var index = 0; index < 19; index++)
                {
                    await one.SynchronizeAsync();
                    var plan = index == 0 ? await one.SubmitAsync(retry.Payload, 0, retry.ClientCommandId) : await Send(one, new PlanCardPayload(1, 0));
                    Assert.True(plan.Accepted); await Send(one, new CancelPlanPayload(plan.PlanCommandId!.Value));
                }
                expected = await actor.GetDiagnosticsAsync(); Assert.Equal(4, (await actor.GetOperationsAsync()).CommandKeys);
            }
            Assert.True(File.Exists(Path.Combine(directory, "journal-head.json")));
            Assert.Equal(6, File.ReadLines(Path.Combine(directory, "commands.jsonl")).Count());
            var journal = new FileMatchJournal(directory, request, options); Assert.Equal(4, journal.Recovery.Commands.Count);
            await using var restored = new MatchActor("rolling", request, new MatchActorOptions(MaximumCommandKeys: 4), journal);
            Assert.Equal(expected, await restored.GetDiagnosticsAsync());
            await using var connection = await restored.ConnectAsync(Audience.PlayerOne); await connection.ReceiveAsync();
            var recent = journal.Recovery.Commands[^1]; await connection.SendAsync(recent.Envelope with { ClientSequence = 99 });
            Assert.Equal(recent.Ack, Assert.IsType<CommandAckPayload>((await connection.ReceiveAsync()).Payload));
            await connection.SendAsync(retry with { ClientSequence = 100 });
            Assert.Equal("PlayerRevisionMismatch", Assert.IsType<CommandAckPayload>((await connection.ReceiveAsync()).Payload).Code);
            Assert.Equal(expected, await restored.GetDiagnosticsAsync());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactionCrashBeforeSegmentReplacementIsRecoverableAndCorruptionIsQuarantined(bool corrupt)
    {
        var directory = NewDirectory(); var request = Fixture.Load();
        try
        {
            // Keep the pre-compaction segment, then restore it to simulate the crash between atomic replacements.
            var options = new FileMatchJournalOptions(16, 16); string prefix; MatchDiagnostics expected;
            await using (var actor = new MatchActor("rolling", request, journal: new FileMatchJournal(directory, request, options)))
            {
                await using var one = new GameClient(await InProcessGameTransport.ConnectAsync(actor, Audience.PlayerOne));
                for (var index = 0; index < 7; index++) { var plan = await Send(one, new PlanCardPayload(1, 0)); await Send(one, new CancelPlanPayload(plan.PlanCommandId!.Value)); }
                var last = await Send(one, new PlanCardPayload(1, 0)); prefix = File.ReadAllText(Path.Combine(directory, "commands.jsonl"));
                await Send(one, new CancelPlanPayload(last.PlanCommandId!.Value)); expected = await actor.GetDiagnosticsAsync();
            }
            using var wrapper = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "journal-head.json")));
            var head = wrapper.RootElement.GetProperty("Data").GetString()!;
            using var anchor = JsonDocument.Parse(head);
            var lastRecord = prefix.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1];
            using var previous = JsonDocument.Parse(lastRecord);
            var previousHash = previous.RootElement.GetProperty("Hash").GetString()!;
            var lastCommand = anchor.RootElement.GetProperty("commands").EnumerateArray().Last().GetRawText();
            var finalData = ContractJson.Serialize(ContractJson.Deserialize<StoredMatchCommand>(lastCommand));
            var finalHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("16\n" + previousHash + "\n" + finalData))).ToLowerInvariant();
            Assert.Equal(anchor.RootElement.GetProperty("logHash").GetString(), finalHash);
            var completePrefix = prefix + JsonSerializer.Serialize(new { Sequence = 16, Previous = previousHash, Data = finalData, Hash = finalHash }) + "\n";
            File.WriteAllText(Path.Combine(directory, "commands.jsonl"), corrupt ? prefix : completePrefix);
            if (corrupt)
            {
                Assert.Throws<InvalidDataException>(() => new FileMatchJournal(directory, request));
                Assert.True(File.Exists(Path.Combine(directory, "quarantined.txt")));
            }
            else
            {
                // Recovery must skip the complete pre-compaction segment, not apply its commands again.
                var restored = new FileMatchJournal(directory, request);
                Assert.Equal(expected.StateHash, MatchStateHasher.Compute(restored.Recovery.State).ToString()); Assert.Contains("logHash", head, StringComparison.Ordinal);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DiagnosticReportIsSeatScopedAndMetricsAndTracingAreObservable()
    {
        var measured = new List<long>(); var activities = new List<Activity>();
        using var listener = new MeterListener(); listener.InstrumentPublished = (instrument, meter) =>
        { if (instrument.Meter.Name == "Eota.Server.Application" && instrument.Name == "eota.match.commands") { meter.EnableMeasurementEvents(instrument); } };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => { lock (measured) { measured.Add(value); } }); listener.Start();
        using var tracing = new ActivityListener { ShouldListenTo = source => source.Name == "Eota.Server.Application",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { lock (activities) { activities.Add(activity); } } };
        ActivitySource.AddActivityListener(tracing);
        await using var actor = new MatchActor("support", Fixture.Load());
        await using var one = new GameClient(await InProcessGameTransport.ConnectAsync(actor, Audience.PlayerOne));
        await Send(one, new PlanCardPayload(1, 0));
        await using var spectator = new GameClient(await InProcessGameTransport.ConnectAsync(actor, Audience.Spectator));
        await spectator.SynchronizeAsync(); var report = await spectator.GetSupportDiagnosticsAsync();
        Assert.DoesNotContain(report.Operations, operation => operation.Kind == nameof(PlanCardPayload));
        var json = ContractJson.Serialize(report);
        foreach (var secret in new[] { "private", "cardInstanceId", "credential", "token", "hand", "rng" }) { Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase); }
        var own = await one.GetSupportDiagnosticsAsync(); Assert.Contains(own.Operations, operation => operation.Kind == nameof(PlanCardPayload));
        Assert.NotEmpty(measured); Assert.NotEmpty(activities);
    }

    private static string NewDirectory() { var directory = Path.Combine(Path.GetTempPath(), "eota-resilience-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); return directory; }
    private static async Task<CommandAckPayload> Send(GameClient client, ClientPayload payload)
    {
        await client.SynchronizeAsync(); var ack = await client.SubmitAsync(payload, client.Store.View!.Private!.CommandRevision);
        Assert.True(ack.Accepted, ack.Code); await client.SynchronizeAsync(); return ack;
    }
}
