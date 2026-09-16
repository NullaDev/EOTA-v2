using System.Diagnostics;
using System.Text.Json;
using Eota.Client.Core;
using Eota.Client.Transport.InProcess;
using Eota.Server.Application;
using Eota.Transport.Contracts;

namespace Eota.Server.IntegrationTests;

public sealed class P10LoadTests
{
    [Fact]
    public async Task ConcurrentMatchesBoundHistoryAndDropSlowObserversWithoutBlockingHealthyPlayers()
    {
        var request = Fixture.Load(); var elapsed = Stopwatch.StartNew();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(async index =>
        {
            await using var actor = new MatchActor("load-" + index, request,
                new MatchActorOptions(MailboxCapacity: 4, ObserverCapacity: 16, MaximumCommandKeys: 16, ReplayCapacity: 8, ReplayBytesPerAudience: 65536));
            await using var slow = await actor.ConnectAsync(Audience.Spectator);
            await using var client = new GameClient(await InProcessGameTransport.ConnectAsync(actor, Audience.PlayerOne));
            for (var step = 0; step < 48; step++)
            {
                await client.SynchronizeAsync(); var revision = client.Store.View!.Private!.CommandRevision;
                var invalid = await client.SubmitAsync(new PlanCardPayload(999999, 0), revision); Assert.False(invalid.Accepted);
                var plan = await client.SubmitAsync(new PlanCardPayload(1, 0), revision); Assert.True(plan.Accepted);
                var cancelled = await client.SubmitAsync(new CancelPlanPayload(plan.PlanCommandId!.Value), plan.PlayerRevision); Assert.True(cancelled.Accepted);
            }
            var report = await actor.GetOperationsAsync();
            Assert.Equal(96, report.Accepted); Assert.Equal(48, report.Rejected); Assert.Equal(1, report.SlowDisconnects);
            Assert.InRange(report.CommandKeys, 1, 16); Assert.InRange(report.ReplayRecords, 0, 24); Assert.InRange(report.ReplayBytes, 0, 3 * 65536);
            Assert.InRange(report.MailboxHighWatermark, 0, 4); Assert.Equal("Active", report.Status);
            return report;
        }));
        var summary = new { matches = results.Length, accepted = results.Sum(r => r.Accepted), rejected = results.Sum(r => r.Rejected),
            slowDisconnects = results.Sum(r => r.SlowDisconnects), elapsedMilliseconds = elapsed.ElapsedMilliseconds,
            maxReplayBytesPerMatch = results.Max(r => r.ReplayBytes), maxCommandKeys = results.Max(r => r.CommandKeys) };
        var path = Path.Combine(Fixture.Root, "artifacts", "p10-load-summary.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(summary));
    }
}
