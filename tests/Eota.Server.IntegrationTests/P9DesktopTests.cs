using System.Text.Json;
using Eota.Client.Desktop;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;
using Eota.Server.Application;
using Eota.Server.Infrastructure;
using Eota.Transport.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Eota.Server.IntegrationTests;

public sealed class P9DesktopTests
{
    [Fact]
    public void CatalogExposesDurabilityAndDerivedCardsFromNestedEffects()
    {
        var catalog = new DesktopCatalog(Fixture.Root);
        Assert.Equal(3, catalog.Cards.Single(card => card.Id == "EOTA-CORE-GUA-FLD-001").Durability);
        Assert.Equal(3, catalog.Cards.Single(card => card.Id == "EOTA-CORE-ART-FLD-003").Durability);
        Assert.All(catalog.Cards.Where(card => card.Kind != "Field"), card => Assert.Null(card.Durability));
        // Covers a summon inside sequence, a conditional sequence and a hand creation.
        Assert.Equal("EOTA-TOKEN-GUA-MIN-001", Assert.Single(catalog.RelatedCards("EOTA-CORE-GUA-SPL-002")).Id);
        Assert.Equal("EOTA-TOKEN-ART-MIN-001", Assert.Single(catalog.RelatedCards("EOTA-CORE-ART-SPL-004")).Id);
        Assert.Equal("EOTA-TOKEN-GUA-MIN-001", Assert.Single(catalog.RelatedCards("EOTA-CORE-GUA-MIN-004")).Id);
        Assert.Empty(catalog.RelatedCards("EOTA-CORE-GUA-FLD-001"));
        Assert.Empty(catalog.RelatedCards("missing"));
        Assert.All(catalog.Cards, card =>
        {
            var related = catalog.RelatedCards(card.Id);
            Assert.Equal(related.Length, related.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.All(related, value => { Assert.Equal("Token", value.Source); Assert.NotEqual(card.Id, value.Id); });
        });
    }

    [Fact]
    public async Task DesktopLocalRemoteAndReplayAgreeIncludingMulliganCancelAndResync()
    {
        var catalog = new DesktopCatalog(Fixture.Root);
        Assert.Equal(282, catalog.Cards.Length);
        foreach (var profession in new[] { "Guardian", "Arcanist", "Artisan", "Hunter" })
        { Assert.Empty(catalog.ValidateDeck(catalog.DefaultDeck(profession))); }
        var settings = new LocalMatchSettings(146, 6, 30, true);
        await using var local = await DesktopSession.LocalAsync(catalog, catalog.DefaultDeck("Guardian"), catalog.DefaultDeck("Arcanist"), settings);
        var directory = Path.Combine(Fixture.Root, "artifacts", "P9DesktopAcceptance");
        await local.SaveReplayAsync(directory);
        await using var host = await Host.Program.BuildAsync(["--urls", "http://127.0.0.1:0", "--fixture", directory,
            "--cards", Path.Combine(Fixture.Root, "Content", "Source", "Cards"), "--seed", "146", "--Logging:LogLevel:Default", "Warning"]);
        await host.StartAsync();
        var address = new Uri(host.Urls.Single());
        Uri Endpoint(string seat) => new UriBuilder(address) { Scheme = "ws", Path = "/matches/p4-demo/ws", Query = "audience=" + seat }.Uri;
        await using var remoteOne = await DesktopSession.RemoteAsync(Endpoint("playerOne"), "p4-demo", catalog.RuleHash);
        await using var remoteTwo = await DesktopSession.RemoteAsync(Endpoint("playerTwo"), "p4-demo", catalog.RuleHash);
        var remotes = new[] { remoteOne, remoteTwo };
        for (var seat = 0; seat < 2; seat++)
        {
            await Both(new SubmitMulliganPayload([local.Client.Store.View!.Private!.Hand[0].CardInstanceId]), remotes[seat]);
            local.SwitchSeat();
            await local.Client.SynchronizeAsync();
        }
        for (var turn = 0; turn < 3; turn++)
        {
            for (var seat = 0; seat < 2; seat++)
            {
                var option = local.Client.Store.View!.Private!.PlanOptions.FirstOrDefault(value => value.Allowed);
                if (option is not null)
                {
                    var card = local.Client.Store.View.Private.Hand.Single(value => value.CardInstanceId == option.CardInstanceId);
                    ClientPayload payload = card.CardKind == "Spell" ? new PlanSpellPayload(card.CardInstanceId, option.LaneId)
                        : new PlanCardPayload(card.CardInstanceId, option.LaneId!.Value);
                    await Both(payload, remotes[seat]);
                    var plan = local.Client.Store.View!.Private!.Planning.Single();
                    await Both(new CancelPlanPayload(plan.PlanCommandId), remotes[seat]);
                    await Both(payload, remotes[seat]);
                }
                await Both(new SubmitTurnPayload(), remotes[seat]);
                local.SwitchSeat(); await local.Client.SynchronizeAsync();
            }
            local.DiscardInactiveFrames(); local.Client.Store.DrainPresentationFrames();
        }
        var actor = host.Services.GetRequiredService<InMemoryMatchDirectory>().Find("p4-demo")!;
        Assert.Equal(local.FinalStateHash, (await actor.GetDiagnosticsAsync()).StateHash);
        await local.SaveReplayAsync(directory);
        var replay = DesktopReplay.Load(catalog, Path.Combine(directory, "replay.json"));
        Assert.Equal(local.FinalStateHash, replay.StateHash);
        Assert.Null(replay.Initial.Private); Assert.Null(replay.Final.Private);
        Assert.NotEmpty(replay.Frames);
        await using var reconnected = await DesktopSession.RemoteAsync(Endpoint("playerOne"), "p4-demo", catalog.RuleHash);
        Assert.Equal(local.Client.Store.ObserverViewHash, reconnected.Client.Store.ObserverViewHash);
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "replay.json")));
        Assert.Contains("submitMulligan", file.RootElement.GetProperty("commands").ToString(), StringComparison.Ordinal);
        Assert.Contains("cancelPlan", file.RootElement.GetProperty("commands").ToString(), StringComparison.Ordinal);

        async Task Both(ClientPayload payload, DesktopSession remote)
        {
            var localAck = await local.SubmitAsync(payload);
            var remoteAck = await remote.SubmitAsync(payload);
            Assert.True(localAck.Accepted, localAck.Code); Assert.True(remoteAck.Accepted, remoteAck.Code);
            await local.Client.SynchronizeAsync(); await remote.Client.SynchronizeAsync();
            Assert.Equal(local.Client.Store.ObserverViewHash, remote.Client.Store.ObserverViewHash);
        }
    }

    [Fact]
    public void LaneStatusesSurviveCheckpointAndAppearForEveryAudience()
    {
        var state = MatchFactory.Create(FileMatchLoader.Load(Fixture.Path, 123456789)).State!;
        state = FrameResolver.Resolve(state,
        [
            new ChangeLaneStatusIntent(new IntentId(1), new LaneId(0), PlayerId.One, LaneStatusKind.Frozen, false, 2),
            new ChangeLaneStatusIntent(new IntentId(2), new LaneId(0), PlayerId.Two, LaneStatusKind.Locked, false, null)
        ]).State;
        var restored = MatchCheckpointCodec.Decode(MatchCheckpointCodec.Encode(state), state.Protocol, state.Content);
        Assert.Equal(MatchStateHasher.Compute(state), MatchStateHasher.Compute(restored));
        foreach (var audience in new[] { Audience.PlayerOne, Audience.PlayerTwo, Audience.Spectator })
        {
            var view = ObserverProjector.Project(restored, audience);
            Assert.True(view.Lanes[0].Frozen); Assert.True(view.Lanes[0].Locked);
            Assert.Equal(2, view.Lanes[0].Statuses.Length);
            var wire = ContractJson.Serialize(view);
            var wireDirectory = Path.Combine(Fixture.Root, "artifacts", "P9Wire");
            Directory.CreateDirectory(wireDirectory);
            File.WriteAllText(Path.Combine(wireDirectory, audience + ".json"), wire);
            var roundtrip = ContractJson.Deserialize<ObserverView>(wire);
            Assert.Equal(ObserverViewHasher.Compute(view), ObserverViewHasher.Compute(roundtrip));
            if (view.Private is not null)
            { Assert.DoesNotContain(view.Private.PlanOptions, value => value.LaneId == 0 && value.Allowed && view.Private.Hand.Single(card => card.CardInstanceId == value.CardInstanceId).CardKind != "Spell"); }
        }
        var intents = new AtomicIntent[] { new DecayLaneStatusesIntent(state.NextIntentId, new LaneId(0), 2) };
        Assert.Equal(FrameResolver.Resolve(state, intents).AfterStateHash, FrameResolver.Resolve(restored, intents).AfterStateHash);
    }
}
