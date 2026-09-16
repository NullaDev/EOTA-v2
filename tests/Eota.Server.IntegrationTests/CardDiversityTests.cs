using System.Collections.Immutable;
using Eota.Content.Compiler;
using Eota.Kernel.Commands;
using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;
using Eota.Kernel.Resolution;
using Eota.Server.Infrastructure;

namespace Eota.Server.IntegrationTests;

public sealed class CardDiversityTests
{
    private static readonly Lazy<RuleContentPack> Rules = new(() => CardContentCompiler.Compile(
        Directory.EnumerateFiles(Path.Combine(Fixture.Root, "Content", "Source", "Cards"), "*.json", SearchOption.AllDirectories)
            .Select(path => new ContentSourceDocument(path, File.ReadAllText(path)))).Content!.Rules);

    [Theory]
    [InlineData("ART-MIN-008", 2, 7)]
    [InlineData("ART-MIN-022", 3, 6)]
    [InlineData("ART-MIN-021", 4, 10)]
    public void LargeMinionsWaitForEverySlowCombatBeforeAttacking(string card, int slow, int attack)
    {
        var state = Play(Create(card), PlayerId.One, card);
        for (var turn = 0; turn < slow; turn++)
        {
            state = Resolve(state);
            Assert.Equal(slow - turn - 1, Assert.IsType<MinionEntityState>(Assert.Single(state.Entities)).SlowTurnsRemaining);
            Assert.Equal(30, state.Players[1].HeroHealth);
        }
        state = Resolve(state);
        Assert.Equal(30 - attack, state.Players[1].HeroHealth);
    }

    [Fact]
    public void SleepingBodyCannotBlockEvenThoughItHasMoreHealthAndAttack()
    {
        var state = Create("GUA-MIN-020", "GUA-MIN-022");
        state = Resolve(Play(Play(state, PlayerId.One, "GUA-MIN-020"), PlayerId.Two, "GUA-MIN-022"));
        Assert.Equal(28, state.Players[0].HeroHealth);
        Assert.Equal(7, state.Entities.OfType<MinionEntityState>().Single(entity => entity.ControllerId == PlayerId.One).CurrentHealth);
    }

    [Theory]
    [InlineData("HUN-MIN-022", "HUN-SPL-015", 5)]
    public void RemovingSlowStillRequiresSwiftToAttackOnEntry(string minion, string wake, int attack)
    {
        var state = Resolve(Play(Play(Create(minion, wake), PlayerId.One, minion), PlayerId.One, wake));
        Assert.Equal(0, state.Entities.OfType<MinionEntityState>().Single().SlowTurnsRemaining);
        Assert.Equal(30, state.Players[1].HeroHealth);
        state = Resolve(state);
        Assert.Equal(30 - attack, state.Players[1].HeroHealth);
    }

    [Fact]
    public void LaunchPaysSelfDamageAndGivesTitanOneImmediateAttack()
    {
        var state = Resolve(Play(Play(Create("ART-MIN-021", "ART-SPL-015"), PlayerId.One, "ART-MIN-021"), PlayerId.One, "ART-SPL-015"));
        var titan = state.Entities.OfType<MinionEntityState>().Single();
        Assert.Equal(0, titan.SlowTurnsRemaining); Assert.Equal(9, titan.CurrentHealth);
        Assert.Equal(20, state.Players[1].HeroHealth);
        Assert.DoesNotContain(titan.Keywords, keyword => keyword.Kind == MinionKeywordKind.Swift);
    }

    [Fact]
    public void DockWakesSameTurnMechanicalEntryWithoutGrantingSwift()
    {
        var state = Resolve(Play(Play(Create("ART-MIN-021", "ART-FLD-007"), PlayerId.One, "ART-MIN-021"), PlayerId.One, "ART-FLD-007"));
        Assert.Equal(0, state.Entities.OfType<MinionEntityState>().Single().SlowTurnsRemaining);
        Assert.Equal(30, state.Players[1].HeroHealth);
        Assert.Equal(20, Resolve(state).Players[1].HeroHealth);
    }

    [Theory]
    [InlineData("ARC-SPL-002", 1)]
    public void ActivationSpellActuallyEnablesNextTurnStudent(string spell, int remainingEther)
    {
        var state = Resolve(Play(Play(Create("ARC-MIN-001", spell), PlayerId.One, "ARC-MIN-001"), PlayerId.One, spell));
        Assert.Equal(remainingEther, state.Lanes[0].PlayerOne.EtherActivation);
        Assert.Equal(0, state.Lanes[0].PlayerTwo.EtherActivation);
        Assert.Equal(30, state.Players[1].HeroHealth);
        Assert.Equal(26, Resolve(state).Players[1].HeroHealth);
    }

    [Fact]
    public void LargeVanillaBeastAcceptsPermanentTribalBuff()
    {
        var state = Resolve(Play(Play(Create("HUN-MIN-021", "HUN-SPL-016"), PlayerId.One, "HUN-MIN-021"), PlayerId.One, "HUN-SPL-016"));
        var beast = state.Entities.OfType<MinionEntityState>().Single();
        Assert.Equal(8, beast.Attack); Assert.Equal(11, beast.MaximumHealth); Assert.Equal(11, beast.CurrentHealth);
        Assert.Equal(22, Resolve(state).Players[1].HeroHealth);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeroPoisonTicksOnlyOnItsBearerAndExpiresAfterThreeEnds(bool reverse)
    {
        var caster = reverse ? PlayerId.Two : PlayerId.One;
        var victim = caster.Opponent;
        var state = Play(Create("HUN-SPL-014"), caster, "HUN-SPL-014");
        for (var tick = 1; tick <= 4; tick++)
        {
            state = Resolve(state);
            Assert.Equal(30, state.Players.Single(player => player.Id == caster).HeroHealth);
            var poisoned = state.Players.Single(player => player.Id == victim);
            Assert.Equal(30 - 2 * Math.Min(tick, 3), poisoned.HeroHealth);
            if (tick < 3) { Assert.Equal(3 - tick, Assert.Single(poisoned.AttachedEffects).RemainingDuration); }
            else { Assert.Empty(poisoned.AttachedEffects); }
        }
    }

    [Fact]
    public void SimultaneousLethalHeroPoisonIsADrawRegardlessOfSubmissionOrder()
    {
        foreach (var reverse in new[] { false, true })
        {
            var state = Create("HUN-SPL-014");
            state = state with { Players = state.Players.Select(player => player with { HeroHealth = 2 }).ToImmutableArray() };
            state = Play(Play(state, PlayerId.One, "HUN-SPL-014"), PlayerId.Two, "HUN-SPL-014");
            state = Resolve(state, reverse);
            Assert.Equal(MatchStatus.Finished, state.Status);
            Assert.Equal("Draw", state.Outcome.ToString());
            Assert.All(state.Players, player => Assert.Equal(0, player.HeroHealth));
        }
    }

    [Fact]
    public void EntryHealingPreventsPoisonLethalButLaterFieldHealingCannotResurrect()
    {
        var state = Create("HUN-SPL-014", "GUA-MIN-023", "HUN-FLD-008");
        state = state with { Players = state.Players.SetItem(1, state.Players[1] with { HeroHealth = 2 }) };
        var healing = Resolve(Play(Play(state, PlayerId.One, "HUN-SPL-014"), PlayerId.Two, "GUA-MIN-023"));
        Assert.Equal(4, healing.Players[1].HeroHealth); Assert.Equal(MatchStatus.Active, healing.Status);
        var late = Resolve(Play(Play(state, PlayerId.One, "HUN-SPL-014"), PlayerId.Two, "HUN-FLD-008"));
        Assert.Equal(0, late.Players[1].HeroHealth); Assert.Equal(MatchStatus.Finished, late.Status);
    }

    [Fact]
    public void StaggeredHeroPoisonsStackExpireSeparatelyAndSurviveCheckpointRestore()
    {
        var state = Resolve(Play(Create("HUN-SPL-014", "HUN-SPL-014"), PlayerId.One, "HUN-SPL-014"));
        Assert.Equal(28, state.Players[1].HeroHealth);
        var restored = MatchCheckpointCodec.Decode(MatchCheckpointCodec.Encode(state), state.Protocol, state.Content);
        Assert.Equal(MatchStateHasher.Compute(state), MatchStateHasher.Compute(restored));
        state = Play(state, PlayerId.One, "HUN-SPL-014");
        restored = Play(restored, PlayerId.One, "HUN-SPL-014");
        foreach (var expected in new[] { (Health: 24, Effects: 2), (Health: 20, Effects: 1), (Health: 18, Effects: 0), (Health: 18, Effects: 0) })
        {
            state = Resolve(state); restored = Resolve(restored);
            Assert.Equal(expected.Health, state.Players[1].HeroHealth);
            Assert.Equal(expected.Effects, state.Players[1].AttachedEffects.Length);
            Assert.Equal(MatchStateHasher.Compute(state), MatchStateHasher.Compute(restored));
        }
    }

    private static string Id(string shortId) => "EOTA-CORE-" + shortId;

    [Fact]
    public void CheapFastActivationProvidesOnlyOneLevelAndDecaysWithoutSupport()
    {
        var state = Resolve(Play(Play(Create("ARC-MIN-001", "ARC-SPL-011"), PlayerId.One, "ARC-MIN-001"), PlayerId.One, "ARC-SPL-011"));
        Assert.Equal(0, state.Lanes[0].PlayerOne.EtherActivation);
        Assert.Equal(28, Resolve(state).Players[1].HeroHealth);
    }

    [Theory]
    [InlineData(false, 30)]
    [InlineData(true, 24)]
    public void PreparedResonanceWakesArcaneGolemAndOnlyLevelThreeGivesEntryAttack(bool levelThree, int enemyHealth)
    {
        var state = Create("ARC-FLD-002", "ARC-SPL-002", "ARC-SPL-011", "ARC-MIN-016");
        state = Play(Play(state, PlayerId.One, "ARC-FLD-002"), PlayerId.One, "ARC-SPL-002");
        if (levelThree) { state = Play(state, PlayerId.One, "ARC-SPL-011"); }
        state = Resolve(state);
        Assert.Equal(levelThree ? 3 : 2, state.Lanes[0].PlayerOne.EtherActivation);
        state = Resolve(Play(state, PlayerId.One, "ARC-MIN-016"));
        Assert.Equal(0, state.Entities.OfType<MinionEntityState>().Single().SlowTurnsRemaining);
        Assert.Equal(enemyHealth, state.Players[1].HeroHealth);
    }

    [Fact]
    public void LevelThreeGiantDestroysThreeLaneTargetsThenConsumesItsWindow()
    {
        var state = Create("ARC-FLD-002", "ARC-SPL-002", "ARC-SPL-011", "ARC-MIN-017", "GUA-MIN-001", "GUA-MIN-013", "GUA-MIN-002", "GUA-MIN-003");
        state = Play(Play(state, PlayerId.One, "ARC-FLD-002", 2), PlayerId.One, "ARC-SPL-002", 2);
        state = Play(Play(state, PlayerId.Two, "GUA-MIN-001", 2), PlayerId.Two, "GUA-MIN-013", 1);
        state = Play(Play(state, PlayerId.Two, "GUA-MIN-002", 3), PlayerId.Two, "GUA-MIN-003", 5);
        state = Resolve(state);
        state = Resolve(Play(Play(state, PlayerId.One, "ARC-MIN-017", 2), PlayerId.One, "ARC-SPL-011", 2));
        Assert.Equal(3, state.Lanes[2].PlayerOne.EtherActivation);
        state = Resolve(state);
        Assert.Equal(new LaneId(5), Assert.Single(state.Entities.OfType<MinionEntityState>(), entity => entity.ControllerId == PlayerId.Two).LaneId);
        Assert.Equal(24, state.Players[1].HeroHealth);
        Assert.Equal(0, state.Lanes[2].PlayerOne.EtherActivation);
        Assert.Equal(20, Resolve(state).Players[1].HeroHealth);
    }

    [Fact]
    public void ResonantBanishSkipsMilitiaDeathGeneration()
    {
        var state = Create("ARC-FLD-002", "ARC-SPL-002", "ARC-SPL-018", "GUA-MIN-008");
        state = Resolve(Play(Play(state, PlayerId.One, "ARC-FLD-002"), PlayerId.One, "ARC-SPL-002"));
        state = Resolve(Play(Play(state, PlayerId.Two, "GUA-MIN-008"), PlayerId.One, "ARC-SPL-018"));
        Assert.DoesNotContain(state.CardInstances, card => card.CurrentPrototypeId.Value == "EOTA-TOKEN-GUA-MIN-001");
        Assert.Equal(EntityRemovalReason.Banish, Assert.Single(state.Tombstones).Reason);
    }

    [Fact]
    public void ExpensiveChargeOrderWakesAndLaunchesANewDefensiveArmy()
    {
        var state = Resolve(Play(Play(Create("GUA-MIN-003", "GUA-SPL-003"), PlayerId.One, "GUA-MIN-003"), PlayerId.One, "GUA-SPL-003"));
        Assert.Equal(22, state.Players[1].HeroHealth);
        var minion = state.Entities.OfType<MinionEntityState>().Single();
        Assert.Equal(0, minion.SlowTurnsRemaining);
        Assert.Equal(7, minion.Attack);
        Assert.Contains(minion.Keywords, keyword => keyword.Kind == MinionKeywordKind.Guard);
        Assert.DoesNotContain(minion.Keywords, keyword => keyword.Kind == MinionKeywordKind.Swift);
    }

    [Fact]
    public void IncubatorSummonsFirstThenPermanentlyGrowsItsOccupantWithResonance()
    {
        var state = Resolve(Play(Play(Create("ARC-FLD-010", "ARC-SPL-002"), PlayerId.One, "ARC-FLD-010"), PlayerId.One, "ARC-SPL-002"));
        var guard = Assert.Single(state.Entities.OfType<MinionEntityState>());
        Assert.Equal(0, guard.Attack); Assert.Equal(3, guard.MaximumHealth);
        state = Resolve(state);
        Assert.Empty(state.Entities.OfType<FieldEntityState>());
        guard = Assert.Single(state.Entities.OfType<MinionEntityState>());
        Assert.Equal(1, guard.Attack); Assert.Equal(4, guard.MaximumHealth);
        Assert.Equal(4, guard.CurrentHealth);
        guard = Assert.Single(Resolve(state).Entities.OfType<MinionEntityState>());
        Assert.Equal(1, guard.Attack); Assert.Equal(4, guard.MaximumHealth);
    }

    [Fact]
    public void PreheatRemovesExactlyOneSlowInAdditionToTheNormalCombatDecay()
    {
        var state = Resolve(Play(Play(Create("ART-MIN-021", "ART-SPL-018"), PlayerId.One, "ART-MIN-021"), PlayerId.One, "ART-SPL-018"));
        var titan = Assert.Single(state.Entities.OfType<MinionEntityState>());
        Assert.Equal(2, titan.SlowTurnsRemaining);
        Assert.Equal(30, state.Players[1].HeroHealth);
    }

    [Fact]
    public void RearGuardDiesIntoAMilitiaThatCanDefendTheSameLane()
    {
        var state = Resolve(Play(Play(Create("GUA-MIN-024", "NEU-SPL-002"), PlayerId.One, "GUA-MIN-024"), PlayerId.Two, "NEU-SPL-002"));
        var militia = Assert.Single(state.Entities.OfType<MinionEntityState>());
        Assert.Equal(PlayerId.One, militia.ControllerId);
        Assert.Contains(militia.Keywords, keyword => keyword.Kind == MinionKeywordKind.Guard);
        Assert.Equal("EOTA-TOKEN-GUA-MIN-001", state.CardInstances.Single(card => card.Id == militia.CardInstanceId).CurrentPrototypeId.Value);
    }

    [Fact]
    public void RiftOwlTurnsMaintainedResonanceIntoDamageAndLifesteal()
    {
        var state = Create("ARC-FLD-002", "ARC-SPL-002", "ARC-MIN-021");
        state = state with { Players = state.Players.SetItem(0, state.Players[0] with { HeroHealth = 20 }) };
        state = Resolve(Play(Play(state, PlayerId.One, "ARC-FLD-002"), PlayerId.One, "ARC-SPL-002"));
        state = Resolve(Play(state, PlayerId.One, "ARC-MIN-021"));
        state = Resolve(state);
        Assert.Equal(25, state.Players[0].HeroHealth);
        Assert.Equal(25, state.Players[1].HeroHealth);
    }

    [Fact]
    public void StargateAndFlameStormSpendTenCostOnAnImmediateLevelThreeSweep()
    {
        var state = Create("ARC-FLD-007", "ARC-SPL-006", "GUA-MIN-001", "GUA-MIN-013", "GUA-MIN-002");
        state = Play(Play(state, PlayerId.One, "ARC-FLD-007", 2), PlayerId.One, "ARC-SPL-006", 2);
        state = Play(Play(Play(state, PlayerId.Two, "GUA-MIN-001", 2), PlayerId.Two, "GUA-MIN-013", 1), PlayerId.Two, "GUA-MIN-002", 3);
        state = Resolve(state);
        Assert.DoesNotContain(state.Entities.OfType<MinionEntityState>(), minion => minion.ControllerId == PlayerId.Two);
        Assert.Equal(24, state.Players[1].HeroHealth);
        Assert.Equal(0, state.Lanes[2].PlayerOne.EtherActivation);
    }

    private static MatchState Create(params string[] cards)
    {
        var protocol = CompiledGameProtocol.Compile(GameProtocolDefinition.DefaultV0 with
        {
            RequiredDeckSize = cards.Length, OpeningHandSize = cards.Length, HandLimit = 12,
            InitialPlayerCost = 20, InitialMaxCost = 20, MaxCostLimit = 20, CardsDrawnPerTurn = 0,
            MulliganEnabled = false, DeckConstructionPolicy = DeckConstructionPolicy.DevelopmentAnySource
        });
        var deck = DeckDefinition.Create(Profession.Neutral, cards.GroupBy(id => id).Select(group => new DeckEntry(new CardPrototypeId(Id(group.Key)), group.Count())));
        return MatchFactory.Create(new(protocol, Rules.Value, 20260915, deck, deck)).State!;
    }

    private static MatchState Play(MatchState state, PlayerId player, string shortId, int lane = 0)
    {
        var card = state.CardInstances.First(card => card.OwnerId == player && card.CurrentPrototypeId.Value == Id(shortId) && card.Zone == CardZone.Hand);
        state.Content.TryGetCard(card.CurrentPrototypeId, out var definition);
        var revision = state.Players.Single(value => value.Id == player).CommandRevision;
        AuthoritativeCommand command = definition is SpellCardDefinition spell
            ? new PlanSpellCommand(player, revision, card.Id, spell.TargetScope == SpellTargetScope.Global ? null : new LaneId(lane))
            : new PlanCardCommand(player, revision, card.Id, new LaneId(lane));
        var result = MatchCommandProcessor.Accept(state, command);
        Assert.True(result.IsAccepted, result.Receipt.RejectionReason.ToString()); return result.State;
    }

    private static MatchState Resolve(MatchState state, bool reverse = false)
    {
        foreach (var player in reverse ? state.Players.Reverse() : state.Players)
        {
            var result = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(player.Id, player.CommandRevision));
            Assert.True(result.IsAccepted); state = result.State;
        }
        var resolved = TurnResolver.ResolveReadyTurn(state);
        Assert.NotEqual(MatchStatus.Failed, resolved.State.Status);
        Assert.DoesNotContain(resolved.Frames.SelectMany(frame => frame.Receipts.Receipts), receipt => receipt.Status == IntentReceiptStatus.Error);
        return resolved.State;
    }
}
