using System.Collections.Immutable;
using Eota.Kernel.Commands;
using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Content.Compiler.Tests;

public sealed partial class SoulweaverRuntimeTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 4)]
    [InlineData(1, 4)]
    public void BurningWraithUsesFrozenAttackAndItsOwnController(int seat, long bonus)
    {
        var owner = seat == 0 ? PlayerId.One : PlayerId.Two;
        var state = Summon(Create(), "M26", owner: owner);
        var id = Minion(state, owner).Id;
        state = Apply(state, [new ModifyMinionStatsIntent(state.NextIntentId, id, bonus, 0)]).State;
        var result = Apply(state, [new KillMinionIntent(state.NextIntentId, id)]);
        Assert.Empty(result.State.Entities);
        Assert.Equal(100 - 3 - bonus, result.State.Players[1 - seat].HeroHealth);
        Assert.Equal(100, result.State.Players[seat].HeroHealth);
    }

    [Fact]
    public void TransformingBurningWraithBypassesItsDeathDamage()
    {
        var state = Summon(Create(), "M26", owner: PlayerId.Two);
        var result = Spell(state, "S12");
        Assert.Equal(Id("T02"), PrototypeOf(result.State, Minion(result.State, PlayerId.Two)));
        Assert.All(result.State.Players, player => Assert.Equal(100, player.HeroHealth));
    }

    [Fact]
    public void SoulOfferingReadsBuffedAttackBeforeRemovalAndThenResolvesDeathDamage()
    {
        var state = Summon(Summon(Create(), "M26"), "dummy", owner: PlayerId.Two);
        state = Apply(state, [new ModifyMinionStatsIntent(state.NextIntentId, Minion(state).Id, 2, 0)]).State;
        var result = Spell(state, "S16");
        Assert.Equal(90, result.State.Players[1].HeroHealth);
        Assert.DoesNotContain(result.State.Entities, entity => entity.ControllerId == PlayerId.One);
        Assert.Equal(20, Minion(result.State, PlayerId.Two).CurrentHealth);
        Assert.Single(result.State.Tombstones, tombstone => tombstone.PrototypeId == Id("M26"));
    }

    [Fact]
    public void ZeroAttackOfferingStillSacrificesTheVesselAndResolvesItsRewards()
    {
        var state = Summon(Create(), "M16");
        state = state with { Players = state.Players.SetItem(0, state.Players[0] with { HeroHealth = 90 }) };
        var result = Spell(state, "S16");
        Assert.Empty(result.State.Entities);
        Assert.Equal(100, result.State.Players[1].HeroHealth);
        Assert.Equal(94, result.State.Players[0].HeroHealth);
        Assert.Equal(2, result.State.Players[0].Hand.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShrineResolvesEntryGhostAndNaturalExpiryZombieWithIndependentSlotChecks(bool occupied)
    {
        var state = occupied ? Summon(Create(), "dummy") : Create();
        state = PlanExpansionCard(state, "F09");
        var turn = EffectRuntimeTests.Resolve(state);
        Assert.True(turn.CompletedTurn);
        Assert.Empty(turn.State.Entities.OfType<FieldEntityState>());
        var survivor = Minion(turn.State);
        Assert.Equal(occupied ? Dummy : Id("T02"), PrototypeOf(turn.State, survivor));
        Assert.Equal(occupied ? 100 : 99, turn.State.Players[1].HeroHealth);
        Assert.Contains(survivor.Keywords, keyword => keyword.Kind == MinionKeywordKind.Guard);
        Assert.Equal(occupied ? 1 : 2, turn.State.Tombstones.Length);
        Assert.DoesNotContain(turn.Frames.SelectMany(frame => frame.Receipts.Receipts), receipt => receipt.Status == IntentReceiptStatus.Error);
    }

    [Fact]
    public void EarlyShrineDestructionCannotReplaceItsLivingGhostOrRetryLater()
    {
        var state = Create();
        state = Apply(state, [new SummonIntent(state.NextIntentId, PlayerId.One, PlayerId.One, new(0), Id("F09"), BattlefieldSlotKind.Field)]).State;
        Assert.Equal(Id("T01"), PrototypeOf(state, Minion(state)));
        var field = state.Entities.OfType<FieldEntityState>().Single();
        state = Apply(state, [new DestroyFieldIntent(state.NextIntentId, field.Id)]).State;
        Assert.Equal(Id("T01"), PrototypeOf(state, Minion(state)));
        var turn = EffectRuntimeTests.Resolve(state);
        Assert.True(turn.CompletedTurn);
        Assert.Empty(turn.State.Entities);
        Assert.Equal(99, turn.State.Players[1].HeroHealth);
    }

    [Theory]
    [InlineData(1, 12)]
    [InlineData(4, 15)]
    [InlineData(5, 16)]
    public void SiphonStitchAddsOnlyActualHealingDamageAndStaysInItsLane(long patientHealth, long enemyHealth)
    {
        var state = Summon(Summon(Summon(Summon(Create(), "M18"), "dummy", owner: PlayerId.Two), "M18", 1), "dummy", 1, PlayerId.Two);
        state = Wound(Wound(state, Minion(state).Id, patientHealth), Minion(state, lane: 1).Id, 1);
        state = PlanExpansionCard(state, "S17");
        var turn = EffectRuntimeTests.Resolve(state);
        Assert.True(turn.CompletedTurn);
        Assert.Equal(enemyHealth, Minion(turn.State, PlayerId.Two).CurrentHealth);
        Assert.Equal(5, Minion(turn.State).CurrentHealth);
        Assert.Equal(20, Minion(turn.State, PlayerId.Two, 1).CurrentHealth);
        Assert.Equal(1, Minion(turn.State, lane: 1).CurrentHealth);
        Assert.All(turn.State.Players, player => Assert.Equal(100, player.HeroHealth));
    }

    [Theory]
    [InlineData("M26", 94)]
    [InlineData("M07", 97)]
    [InlineData("T02", 97)]
    public void UrgeEnablesEntryTurnAttackAndKillsTheCarrierAtTurnEnd(string code, long enemyHealth)
    {
        var state = PlanExpansionCard(PlanExpansionCard(Create(), code), "S18");
        var turn = EffectRuntimeTests.Resolve(state);
        Assert.True(turn.CompletedTurn);
        Assert.Empty(turn.State.Entities);
        Assert.Equal(enemyHealth, turn.State.Players[1].HeroHealth);
        Assert.Empty(turn.State.Players[0].Hand);
        Assert.Single(turn.State.Tombstones);
    }

    [Fact]
    public void UrgeWorksOnNonUndeadAndDoesNotPassSelfDestructionToTheDeathToken()
    {
        var state = PlanExpansionCard(PlanExpansionCard(Create(), "M03"), "S18");
        var first = EffectRuntimeTests.Resolve(state);
        Assert.True(first.CompletedTurn);
        var zombie = Minion(first.State);
        Assert.Equal(Id("T02"), PrototypeOf(first.State, zombie));
        Assert.Empty(zombie.AttachedEffects);
        Assert.Contains(zombie.Keywords, keyword => keyword.Kind == MinionKeywordKind.Guard);
        Assert.DoesNotContain(zombie.Keywords, keyword => keyword.Kind == MinionKeywordKind.Swift);
        Assert.Equal(97, first.State.Players[1].HeroHealth);
        var second = EffectRuntimeTests.Resolve(first.State);
        Assert.True(second.CompletedTurn);
        Assert.Equal(zombie.Id, Minion(second.State).Id);
        Assert.Equal(97, second.State.Players[1].HeroHealth);
    }

    [Fact]
    public void UrgeStillKillsASlowCarrierWithoutGrantingItAnAttack()
    {
        var state = Summon(Create(), "M26");
        state = state with { Entities = state.Entities.Select(entity => entity is MinionEntityState minion
            ? minion with { SlowTurnsRemaining = 1, Keywords = minion.Keywords.Add(new(MinionKeywordKind.Slow, 1)) } : entity).ToImmutableArray() };
        var turn = EffectRuntimeTests.Resolve(PlanExpansionCard(state, "S18"));
        Assert.True(turn.CompletedTurn);
        Assert.Empty(turn.State.Entities);
        Assert.Equal(97, turn.State.Players[1].HeroHealth);
    }

    [Fact]
    public void UrgedGhostDiesOnceAndPaysDeathObserversOnlyOnce()
    {
        var state = Summon(Summon(Create(), "T01"), "F08");
        var turn = EffectRuntimeTests.Resolve(PlanExpansionCard(state, "S18"));
        Assert.True(turn.CompletedTurn);
        Assert.Empty(turn.State.Entities.OfType<MinionEntityState>());
        Assert.Single(turn.State.Tombstones);
        Assert.Single(turn.State.Players[0].Hand);
        Assert.Equal(99, turn.State.Players[1].HeroHealth);
    }

    private static CardPrototypeId PrototypeOf(MatchState state, BattlefieldEntityState entity)
        => state.CardInstances.Single(card => card.Id == entity.CardInstanceId).CurrentPrototypeId;

    private static MatchState PlanExpansionCard(MatchState state, string code)
    {
        state = FrameResolver.Resolve(state, [new HandCardIntent(state.NextIntentId, PlayerId.One, HandRequestKind.Generate, new(0, "expansion", 0), Id(code))]).State;
        var player = state.Players[0];
        var card = player.Hand.Last();
        AuthoritativeCommand command = code[0] == 'S'
            ? new PlanSpellCommand(PlayerId.One, player.CommandRevision, card, new(0))
            : new PlanCardCommand(PlayerId.One, player.CommandRevision, card, new(0));
        var accepted = MatchCommandProcessor.Accept(state, command);
        Assert.True(accepted.IsAccepted, accepted.Receipt.RejectionReason.ToString());
        return accepted.State;
    }
}
