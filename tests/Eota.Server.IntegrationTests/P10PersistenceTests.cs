using System.Threading.Channels;
using Eota.Client.Core;
using Eota.Client.Transport.InProcess;
using Eota.Kernel.Matches;
using Eota.Server.Application;
using Eota.Server.Infrastructure;
using Eota.Transport.Contracts;

namespace Eota.Server.IntegrationTests;

public sealed class P10PersistenceTests
{
    [Fact]
    public async Task CheckpointAndTailRecoverHashAndExactlyOnceAcknowledgement()
    {
        var directory = NewDirectory(); var request = Fixture.Load();
        try
        {
            MatchDiagnostics expected;
            var retry = new ClientEnvelope(0, "durable", "original-plan", 1, 0, new PlanCardPayload(1, 0));
            CommandAckPayload original;
            await using (var actor = new MatchActor("durable", request, journal: new FileMatchJournal(directory, request)))
            {
                await using (var first = await actor.ConnectAsync(Audience.PlayerOne))
                {
                    await first.ReceiveAsync(); await first.SendAsync(retry);
                    original = Assert.IsType<CommandAckPayload>((await first.ReceiveAsync()).Payload); Assert.True(original.Accepted);
                }
                await using var one = new GameClient(await InProcessGameTransport.ConnectAsync(actor, Audience.PlayerOne));
                await using var two = new GameClient(await InProcessGameTransport.ConnectAsync(actor, Audience.PlayerTwo));
                await one.SynchronizeAsync();
                await Send(one, new CancelPlanPayload(original.PlanCommandId!.Value));
                for (var i = 0; i < 10; i++)
                {
                    var plan = await Send(one, new PlanCardPayload(1, 0)); await Send(one, new CancelPlanPayload(plan.PlanCommandId!.Value));
                }
                await Send(one, new SubmitTurnPayload()); await Send(two, new SubmitTurnPayload());
                expected = await actor.GetDiagnosticsAsync();
            }
            Assert.True(File.Exists(Path.Combine(directory, "checkpoint.json")));
            await using var restored = new MatchActor("durable", request, journal: new FileMatchJournal(directory, request));
            Assert.Equal(expected, await restored.GetDiagnosticsAsync());
            await using var connection = await restored.ConnectAsync(Audience.PlayerOne);
            await connection.ReceiveAsync(); await connection.SendAsync(retry with { ClientSequence = 200 });
            Assert.Equal(original, Assert.IsType<CommandAckPayload>((await connection.ReceiveAsync()).Payload));
            Assert.Equal(expected, await restored.GetDiagnosticsAsync());
            await connection.SendAsync(retry with { ClientSequence = 201, Payload = new SubmitTurnPayload() });
            Assert.Equal("client-command-id-conflict", Assert.IsType<CommandAckPayload>((await connection.ReceiveAsync()).Payload).Code);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("truncated")]
    [InlineData("wrong-seed")]
    public async Task DamagedOrForeignJournalIsIsolatedWithoutOverwritingEvidence(string damage)
    {
        var directory = NewDirectory(); var request = Fixture.Load();
        try
        {
            await using (var actor = new MatchActor("durable", request, journal: new FileMatchJournal(directory, request)))
            {
                await using var one = new GameClient(await InProcessGameTransport.ConnectAsync(actor, Audience.PlayerOne));
                await Send(one, new PlanCardPayload(1, 0));
            }
            var path = Path.Combine(directory, "commands.jsonl"); var original = File.ReadAllText(path);
            if (damage == "hash") { File.WriteAllText(path, original.Replace("\"Sequence\":1", "\"Sequence\":2", StringComparison.Ordinal)); }
            if (damage == "truncated") { File.WriteAllText(path, original[..^15]); }
            if (damage == "wrong-seed") { request = request with { MatchSeed = 999 }; }
            var evidence = File.ReadAllText(path);
            Assert.Throws<InvalidDataException>(() => new FileMatchJournal(directory, request));
            Assert.True(File.Exists(Path.Combine(directory, "quarantined.txt"))); Assert.Equal(evidence, File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task JournalFailureCannotAcknowledgeAnUndurableCommandOrFaultAnotherMatch()
    {
        var request = Fixture.Load(); var journal = new FailingJournal(MatchFactory.Create(request).State!);
        await using var faulty = new MatchActor("bad", request, journal: journal); await using var healthy = new MatchActor("good", request);
        var baseline = await healthy.GetDiagnosticsAsync();
        await using var connection = await faulty.ConnectAsync(Audience.PlayerOne); await connection.ReceiveAsync();
        await Assert.ThrowsAsync<IOException>(() => connection.SendAsync(new ClientEnvelope(0, "bad", "p", 1, 0, new PlanCardPayload(1, 0))));
        await Assert.ThrowsAsync<ChannelClosedException>(async () => await connection.ReceiveAsync());
        Assert.Equal("ServerFaulted", (await faulty.GetDiagnosticsAsync()).Status); Assert.Equal(baseline, await healthy.GetDiagnosticsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => faulty.ConnectAsync(Audience.PlayerOne));
    }

    private sealed class FailingJournal(MatchState state) : IMatchJournal
    {
        public MatchRecovery Recovery { get; } = new(state, []);
        public void Commit(StoredMatchCommand command, MatchState updated) => throw new IOException("disk-full");
    }

    private static string NewDirectory() { var path = Path.Combine(Path.GetTempPath(), "eota-journal-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static async Task<CommandAckPayload> Send(GameClient client, ClientPayload payload)
    {
        await client.SynchronizeAsync(); var ack = await client.SubmitAsync(payload, client.Store.View!.Private!.CommandRevision);
        Assert.True(ack.Accepted, ack.Code); await client.SynchronizeAsync(); return ack;
    }
}
