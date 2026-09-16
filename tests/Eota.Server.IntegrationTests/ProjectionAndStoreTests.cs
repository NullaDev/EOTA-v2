using System.Collections.Immutable;
using Eota.Client.Core;
using Eota.Kernel.Commands;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;
using Eota.Kernel.Resolution;
using Eota.Server.Application;
using Eota.Transport.Contracts;

namespace Eota.Server.IntegrationTests;

public sealed class ProjectionAndStoreTests
{
    [Fact]
    public void HiddenDeckOrderAndRngChangeFullHashButNeverObserverHashes()
    {
        var request = Fixture.Load();
        request = request with { Protocol = CompiledGameProtocol.Compile(request.Protocol.Definition with { OpeningHandSize = 2 }) };
        var initial = MatchFactory.Create(request).State!;
        var altered = initial with
        {
            Players = initial.Players.Select(value => value with { Deck = value.Deck.Reverse().ToImmutableArray() }).ToImmutableArray(),
            RuleRng = Eota.Kernel.Determinism.GlobalRuleRng.Create(987654321)
        };
        Assert.NotEqual(MatchStateHasher.Compute(initial), MatchStateHasher.Compute(altered));
        foreach (var audience in Enum.GetValues<Audience>())
        {
            Assert.Equal(ObserverViewHasher.Compute(ObserverProjector.Project(initial, audience)),
                ObserverViewHasher.Compute(ObserverProjector.Project(altered, audience)));
        }
    }

    [Fact]
    public void DrawIdentityIsOnlyProjectedToDrawingPlayerIncludingEvents()
    {
        var request = Fixture.Load();
        request = request with { Protocol = CompiledGameProtocol.Compile(request.Protocol.Definition with { OpeningHandSize = 2 }) };
        var initial = MatchFactory.Create(request).State!;
        var replay = MatchReplay.ReplayCommands(initial, [new SubmitTurnCommand(PlayerId.One, 0), new SubmitTurnCommand(PlayerId.Two, 0)]);
        var drawFrame = replay.Frames.Single(frame => frame.Events.Events.Any(value => value.Kind == DomainEventKind.CardDrawn));
        foreach (var audience in Enum.GetValues<Audience>())
        {
            var events = ObserverProjector.ProjectEvents(drawFrame, audience).Where(value => value.Kind == "CardDrawn").ToArray();
            Assert.Equal(2, events.Length);
            foreach (var value in events)
            {
                var ownsCard = audience != Audience.Spectator && value.PlayerId == (audience == Audience.PlayerOne ? 0 : 1);
                Assert.Equal(ownsCard, value.Card is not null);
            }
        }
    }

    [Fact]
    public void MulliganSelectionStaysPrivateUntilBothPlayersSubmit()
    {
        var request = Fixture.Load();
        request = request with { Protocol = CompiledGameProtocol.Compile(request.Protocol.Definition with { OpeningHandSize = 2, MulliganEnabled = true }) };
        var initial = MatchFactory.Create(request).State!;
        var card = initial.Players[0].Hand[0];
        var first = MatchCommandProcessor.Accept(initial, new SubmitMulliganCommand(PlayerId.One, 0, [card])).State;
        var alternative = MatchCommandProcessor.Accept(initial, new SubmitMulliganCommand(PlayerId.One, 0, [])).State;
        foreach (var audience in new[] { Audience.PlayerTwo, Audience.Spectator })
        {
            Assert.Equal(ObserverViewHasher.Compute(ObserverProjector.Project(first, audience)),
                ObserverViewHasher.Compute(ObserverProjector.Project(alternative, audience)));
        }
        Assert.Equal(card.Value, Assert.Single(ObserverProjector.Project(first, Audience.PlayerOne).Private!.MulliganSelection));
    }

    [Fact]
    public void ClientDetectsGapsDuplicatesAndTamperingThenRecoversFromSnapshot()
    {
        var view = ObserverProjector.Project(MatchFactory.Create(Fixture.Load()).State!, Audience.PlayerOne);
        var snapshot = new ObserverSnapshotPayload(null, view, ObserverViewHasher.Compute(view));
        var store = new ObserverStore("p4");
        Assert.True(store.Apply(new ServerEnvelope(0, "p4", 1, 0, snapshot)));
        var frame = new PresentationFramePayload(1, view, snapshot.ObserverViewHash, []);
        Assert.False(store.Apply(new ServerEnvelope(0, "p4", 3, 1, frame)));
        Assert.True(store.NeedsSnapshot);
        Assert.True(store.Apply(new ServerEnvelope(0, "p4", 4, 1, snapshot)));
        Assert.False(store.Apply(new ServerEnvelope(0, "p4", 4, 1, snapshot)));
        Assert.True(store.Apply(new ServerEnvelope(0, "p4", 5, 1, snapshot)));
        Assert.False(store.Apply(new ServerEnvelope(0, "p4", 6, 2, frame with { ObserverViewHash = new string('0', 64) })));
        Assert.Equal(1UL, store.MatchRevision);
        Assert.True(store.Apply(new ServerEnvelope(0, "p4", 7, 2, snapshot)));
        Assert.False(store.NeedsSnapshot);
        Assert.Empty(store.DrainPresentationFrames());
        Assert.False(store.Apply(new ServerEnvelope(0, "wrong-match", 8, 2, snapshot)));
    }

    [Fact]
    public void ClientBoundsPresentationBacklogAndKeepsAudienceFixed()
    {
        var state = MatchFactory.Create(Fixture.Load()).State!;
        var view = ObserverProjector.Project(state, Audience.PlayerOne);
        var snapshot = new ObserverSnapshotPayload(null, view, ObserverViewHasher.Compute(view));
        var store = new ObserverStore("p4", presentationCapacity: 1);
        Assert.True(store.Apply(new ServerEnvelope(0, "p4", 1, 0, snapshot)));
        var frame = new PresentationFramePayload(1, view, snapshot.ObserverViewHash, []);
        Assert.True(store.Apply(new ServerEnvelope(0, "p4", 2, 1, frame)));
        Assert.False(store.Apply(new ServerEnvelope(0, "p4", 3, 2, frame)));
        Assert.True(store.NeedsSnapshot);
        Assert.Empty(store.DrainPresentationFrames());
        Assert.True(store.Apply(new ServerEnvelope(0, "p4", 4, 2, snapshot)));
        var opponent = ObserverProjector.Project(state, Audience.PlayerTwo);
        Assert.False(store.Apply(new ServerEnvelope(0, "p4", 5, 2,
            new ObserverSnapshotPayload(null, opponent, ObserverViewHasher.Compute(opponent)))));
    }
}
