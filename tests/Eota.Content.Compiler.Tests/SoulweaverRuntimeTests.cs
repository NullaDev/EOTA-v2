using System.Collections.Immutable;
using Eota.ContentCli;
using Eota.Kernel.Commands;
using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;
using Eota.Kernel.Resolution;

namespace Eota.Content.Compiler.Tests;

public sealed partial class SoulweaverRuntimeTests
{
    private static readonly RuleContentPack Rules = LoadRules();
    private static readonly CardPrototypeId Dummy = new("TEST-SOUL-DUMMY");

    [Theory]
    [InlineData(1, 3)]
    [InlineData(4, 0)]
    public void HealingUsesActualRestorationAndFullHealthDoesNotTrigger(long health, long restored)
    {
        var state = Summon(Create(), "M13");
        var minion = Minion(state);
        state = Wound(state, minion.Id, health);
        var result = Apply(state, [new HealMinionIntent(state.NextIntentId, minion.Id, 9)]);
        Assert.Equal(4, Minion(result.State).CurrentHealth);
        Assert.Equal(restored > 0 ? 2 : 1, Minion(result.State).Attack);
        Assert.Equal(restored, result.Frames.SelectMany(frame => frame.Events.Events).Where(fact => fact.Kind == DomainEventKind.EntityHealed).Sum(fact => fact.CurrentValue ?? 0));
    }

    [Fact]
    public void ParallelHealingAggregatesOnceButSeparateFramesCanTriggerAgain()
    {
        var state = Summon(Create(), "M13"); var minion = Minion(state);
        state = Wound(state, minion.Id, 1);
        AtomicIntent[] intents = [new HealMinionIntent(state.NextIntentId, minion.Id, 2), new HealMinionIntent(new(state.NextIntentId.Value + 1), minion.Id, 2)];
        var result = Apply(state, intents);
        Assert.Equal(2, Minion(result.State).Attack);
        Assert.Equal(3, Assert.Single(result.Frames[0].Events.Events, fact => fact.Kind == DomainEventKind.EntityHealed).CurrentValue);
        Assert.Equal(result.Frames[0].AfterStateHash, FrameResolver.Resolve(state, intents.Reverse()).AfterStateHash);
        var wounded = Wound(result.State, minion.Id, 3);
        Assert.Equal(3, Minion(Apply(wounded, [new HealMinionIntent(wounded.NextIntentId, minion.Id, 1)]).State).Attack);
    }

    [Fact]
    public void MaximumHealthIncreaseIsNotHealing()
    {
        var state = Summon(Create(), "M13"); var minion = Minion(state);
        var result = Apply(state, [new ModifyMinionStatsIntent(state.NextIntentId, minion.Id, 0, 3)]);
        Assert.Equal(1, Minion(result.State).Attack);
        Assert.Equal(7, Minion(result.State).CurrentHealth);
        Assert.DoesNotContain(result.Frames.SelectMany(frame => frame.Events.Events), fact => fact.Kind == DomainEventKind.EntityHealed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void HeroHealingStillTriggersWhenSameFrameDamageMakesTheNetDeltaZero(int seat)
    {
        var owner = seat == 0 ? PlayerId.One : PlayerId.Two;
        var state = Summon(Create(), "M20", owner: owner);
        AtomicIntent[] intents = [new DamageHeroIntent(state.NextIntentId, owner, 4), new HealHeroIntent(new(state.NextIntentId.Value + 1), owner, 4)];
        var result = Apply(state, intents);
        Assert.Equal(100, result.State.Players[seat].HeroHealth);
        Assert.Equal(96, result.State.Players[1 - seat].HeroHealth);
        Assert.DoesNotContain(result.Frames[0].Events.Events, fact => fact.Kind == DomainEventKind.HeroHealthChanged);
        Assert.Equal(4, Assert.Single(result.Frames[0].Events.Events, fact => fact.Kind == DomainEventKind.HeroHealed).CurrentValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeadPatientsAndDeadObserversDoNotReceiveHealingTriggers(bool killPatient)
    {
        var state = Summon(Summon(Create(), "M13"), "F03"); var minion = Minion(state);
        state = Wound(state, minion.Id, 1);
        var field = state.Entities.OfType<FieldEntityState>().Single();
        AtomicIntent kill = killPatient ? new KillMinionIntent(new(state.NextIntentId.Value + 1), minion.Id)
            : new DestroyFieldIntent(new(state.NextIntentId.Value + 1), field.Id);
        var result = Apply(state, [new HealMinionIntent(state.NextIntentId, minion.Id, 2), kill]);
        if (killPatient) { Assert.Empty(result.State.Entities.OfType<MinionEntityState>()); Assert.Single(result.Frames); }
        else { Assert.Equal(2, Minion(result.State).Attack); Assert.Equal(4, Minion(result.State).MaximumHealth); }
    }

    [Fact]
    public void HeroObserverKilledInTheHealingFrameCannotBurnTheOpponent()
    {
        var state = Summon(Create(), "M20");
        state = state with { Players = state.Players.SetItem(0, state.Players[0] with { HeroHealth = 90 }) };
        var result = Apply(state, [new HealHeroIntent(state.NextIntentId, PlayerId.One, 4), new KillMinionIntent(new(state.NextIntentId.Value + 1), Minion(state).Id)]);
        Assert.Equal(94, result.State.Players[0].HeroHealth);
        Assert.Equal(100, result.State.Players[1].HeroHealth);
    }

    [Fact]
    public void HealingAltarOnlyBuffsThePatientInItsLaneAndDoesNotLoop()
    {
        var state = Summon(Summon(Summon(Create(), "M13"), "M13", 1), "F03");
        var first = Minion(state); var second = Minion(state, lane: 1);
        state = Wound(Wound(state, first.Id, 1), second.Id, 1);
        var result = Apply(state, [new HealMinionIntent(state.NextIntentId, first.Id, 2), new HealMinionIntent(new(state.NextIntentId.Value + 1), second.Id, 2)]);
        Assert.Equal((3L, 4L, 5L), (Minion(result.State).Attack, Minion(result.State).CurrentHealth, Minion(result.State).MaximumHealth));
        Assert.Equal((2L, 3L, 4L), (Minion(result.State, lane: 1).Attack, Minion(result.State, lane: 1).CurrentHealth, Minion(result.State, lane: 1).MaximumHealth));
        Assert.Equal(2, result.Frames.Length);
    }

    [Fact]
    public void PatientHealingCascadesThroughHeroHealingUsingBothActualCaps()
    {
        var state = Summon(Summon(Summon(Create(), "M15"), "M20", 2), "M19", 1);
        state = Summon(state, "dummy", 1, PlayerId.Two);
        state = Wound(state, Minion(state).Id, 1);
        state = state with { Players = state.Players.SetItem(0, state.Players[0] with { HeroHealth = 98 }) };
        var result = Spell(state, "S02");
        Assert.Equal(4, Minion(result.State).CurrentHealth);
        Assert.Equal(100, result.State.Players[0].HeroHealth);
        Assert.Equal(98, result.State.Players[1].HeroHealth);
        Assert.Equal(18, Minion(result.State, PlayerId.Two, 1).CurrentHealth);
        Assert.Equal(2, Assert.Single(result.Frames.SelectMany(frame => frame.Events.Events), fact => fact.Kind == DomainEventKind.HeroHealed).CurrentValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WraithSacrificeAlwaysLeavesAZombieAndAlsoRemovesTheLaneEnemy(bool enemy)
    {
        var state = Summon(Create(), "M08");
        if (enemy) { state = Summon(state, "dummy", owner: PlayerId.Two); }
        var result = Spell(state, "S01");
        Assert.Equal(Id("T02"), result.State.CardInstances.Single(card => card.Id == Minion(result.State).CardInstanceId).CurrentPrototypeId);
        Assert.Empty(result.State.Entities.Where(entity => entity.ControllerId == PlayerId.Two));
        Assert.Equal(2, result.State.Players[0].Hand.Length);
        Assert.Empty(Minion(result.State).AttachedEffects);
    }

    [Fact]
    public void VesselAndSacrificeDrawFourAndHealFourWithoutLeavingAMinion()
    {
        var state = Summon(Create(), "M16");
        state = state with { Players = state.Players.SetItem(0, state.Players[0] with { HeroHealth = 90 }) };
        var result = Spell(state, "S01");
        Assert.Empty(result.State.Entities);
        Assert.Equal(4, result.State.Players[0].Hand.Length);
        Assert.Equal(94, result.State.Players[0].HeroHealth);
    }

    [Fact]
    public void CurseBelongsToItsCarrierAndCanTriggerFromItsOwnDamage()
    {
        var state = Summon(Create(), "T02", owner: PlayerId.Two);
        var result = Spell(state, "S06");
        Assert.Empty(result.State.Entities);
        Assert.Equal(100, result.State.Players[0].HeroHealth);
        Assert.Equal(97, result.State.Players[1].HeroHealth);
    }

    [Fact]
    public void CommanderGrantsDeathSummonsInTheCarriersLanes()
    {
        var state = Summon(Summon(Summon(Create(), "dummy", 0), "M25", 1), "dummy", 2);
        var commander = Minion(state, lane: 1);
        var result = Invoke(state, commander, EffectTriggerKind.SelfEntered);
        Assert.Single(Minion(result.State).AttachedEffects);
        Assert.Empty(Minion(result.State, lane: 1).AttachedEffects);
        result = Apply(result.State, [new KillMinionIntent(result.State.NextIntentId, Minion(result.State).Id), new KillMinionIntent(new(result.State.NextIntentId.Value + 1), Minion(result.State, lane: 2).Id)]);
        Assert.Equal(3, Minion(result.State).Attack);
        Assert.Equal(3, Minion(result.State, lane: 2).Attack);
        Assert.All(result.State.Entities, entity => Assert.Empty(entity.AttachedEffects));
    }

    [Fact]
    public void TemporaryPrayerAppliesBeforeItsHealAndExpiresAtTurnEnd()
    {
        var state = Summon(Create(), "M17"); state = Wound(state, Minion(state).Id, 1);
        var result = Spell(state, "S14");
        Assert.Equal(98, result.State.Players[1].HeroHealth);
        Assert.Single(Minion(result.State).AttachedEffects);
        // The minion's ordinary turn-end heal is another frame and triggers the temporary prayer.
        var turn = EffectRuntimeTests.Resolve(result.State);
        Assert.Equal(97, turn.State.Players[1].HeroHealth);
        Assert.Empty(Minion(turn.State).AttachedEffects);
    }

    [Fact]
    public void OrdersOnlyMobilizeUndeadAndRestoreTheirPrintedKeywords()
    {
        var state = Summon(Summon(Create(), "T02"), "dummy", 1);
        var result = Spell(state, "S13");
        Assert.Equal(4, Minion(result.State).Attack);
        Assert.Contains(Minion(result.State).Keywords, value => value.Kind == MinionKeywordKind.Swift);
        Assert.DoesNotContain(Minion(result.State).Keywords, value => value.Kind == MinionKeywordKind.Guard);
        Assert.Equal(0, Minion(result.State, lane: 1).Attack);
        Assert.Contains(Minion(result.State, lane: 1).Keywords, value => value.Kind == MinionKeywordKind.Guard);
        var turn = EffectRuntimeTests.Resolve(result.State);
        Assert.Equal(96, turn.State.Players[1].HeroHealth);
        Assert.Equal(3, Minion(turn.State).Attack);
        Assert.Contains(Minion(turn.State).Keywords, value => value.Kind == MinionKeywordKind.Guard);
        Assert.DoesNotContain(Minion(turn.State).Keywords, value => value.Kind == MinionKeywordKind.Swift);
    }

    [Fact]
    public void HealingRemovesGuardTemporarilyWithoutBypassingEntryRestriction()
    {
        var state = Summon(Create(), "M23"); state = Wound(state, Minion(state).Id, 3);
        var result = Spell(state, "S02");
        Assert.DoesNotContain(Minion(result.State).Keywords, value => value.Kind == MinionKeywordKind.Guard);
        var turn = EffectRuntimeTests.Resolve(result.State);
        Assert.Equal(100, turn.State.Players[1].HeroHealth);
        Assert.Contains(Minion(turn.State).Keywords, value => value.Kind == MinionKeywordKind.Guard);
    }

    [Fact]
    public void OrdinaryGhostExpiresButGhostBornAfterMinionEndWindowWaitsUntilNextTurn()
    {
        var normal = EffectRuntimeTests.Resolve(Summon(Create(), "T01"));
        Assert.Empty(normal.State.Entities);
        Assert.Equal(99, normal.State.Players[1].HeroHealth);
        var late = EffectRuntimeTests.Resolve(Summon(Summon(Create(), "M02"), "F04"));
        Assert.Equal(Id("T01"), late.State.CardInstances.Single(card => card.Id == Minion(late.State).CardInstanceId).CurrentPrototypeId);
        Assert.Equal(2, late.State.Players[0].Hand.Length);
        var next = EffectRuntimeTests.Resolve(late.State);
        Assert.Empty(next.State.Entities.OfType<MinionEntityState>());
        Assert.Equal(99, next.State.Players[1].HeroHealth);
        Assert.Equal(2, next.State.Players[0].Hand.Length);
    }

    [Fact]
    public void PlayedHealingSpellRunsThroughTheAuthoritativeTurnPipeline()
    {
        var state = Summon(Create(), "M18"); state = Summon(state, "dummy", owner: PlayerId.Two);
        state = Wound(state, Minion(state).Id, 2);
        state = FrameResolver.Resolve(state, [new HandCardIntent(state.NextIntentId, PlayerId.One, HandRequestKind.Generate, new(0, "test", 0), Id("S02"))]).State;
        var card = state.Players[0].Hand.Single();
        var plan = MatchCommandProcessor.Accept(state, new PlanSpellCommand(PlayerId.One, 0, card, new(0)));
        Assert.True(plan.IsAccepted);
        var turn = EffectRuntimeTests.Resolve(plan.State);
        Assert.True(turn.CompletedTurn);
        Assert.Equal(17, Minion(turn.State, PlayerId.Two).CurrentHealth);
        Assert.Equal(5, Minion(turn.State).CurrentHealth);
        Assert.Empty(turn.State.Players[0].Hand);
    }

    private static CardPrototypeId Id(string code) => code == "dummy" ? Dummy : new($"EOTA-{(code[0] == 'T' ? "TOKEN" : "CORE")}-SOU-{(code[0] is 'M' or 'T' ? "MIN" : code[0] == 'F' ? "FLD" : "SPL")}-{int.Parse(code[1..], System.Globalization.CultureInfo.InvariantCulture):000}");
    private static MinionEntityState Minion(MatchState state, PlayerId? owner = null, int lane = 0) => state.Entities.OfType<MinionEntityState>().Single(entity => entity.ControllerId == (owner ?? PlayerId.One) && entity.LaneId.Value == lane);
    private static MatchState Wound(MatchState state, EntityId id, long health) => state with { Entities = state.Entities.Select(entity => entity is MinionEntityState minion && entity.Id == id ? minion with { CurrentHealth = health } : entity).ToImmutableArray() };
    private static MatchState Summon(MatchState state, string code, int lane = 0, PlayerId? owner = null)
        => FrameResolver.Resolve(state, [new SummonIntent(state.NextIntentId, owner ?? PlayerId.One, owner ?? PlayerId.One, new(lane), Id(code), code[0] == 'F' ? BattlefieldSlotKind.Field : BattlefieldSlotKind.Minion)]).State;
    private static MatchState Create()
    {
        var cards = Rules.Cards.Where(card => card.Profession == Profession.Soulweaver || card.Id == Dummy).ToArray();
        var protocol = CompiledGameProtocol.Compile(GameProtocolDefinition.DefaultV0 with { RequiredDeckSize = cards.Length, OpeningHandSize = 0, HandLimit = 500,
            InitialHeroHealth = 100, InitialPlayerCost = 100, InitialMaxCost = 100, MaxCostLimit = 100, CardsDrawnPerTurn = 0, DeckConstructionPolicy = DeckConstructionPolicy.DevelopmentAnySource });
        var deck = DeckDefinition.Create(Profession.Soulweaver, cards.Select(card => new DeckEntry(card.Id, 1)));
        return MatchFactory.Create(new(protocol, Rules, 123456789, deck, deck)).State!;
    }
    private static RuleContentPack LoadRules()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Eota.sln"))) { directory = directory.Parent; }
        return RuleContentPack.Create("eota.card/v2", ContentCatalog.Load(directory!.FullName).Bundle.Rules.Cards.Append<CardDefinition>(
            new MinionCardDefinition(new("TEST-SOUL-DUMMY"), CardSource.Test, Profession.Neutral, 0, 0, 20, [new(MinionKeywordKind.Guard)])));
    }
    private static (MatchState State, ImmutableArray<FrameTransition> Frames) Spell(MatchState state, string code)
    {
        var card = state.CardInstances.First(card => card.OwnerId == PlayerId.One && card.CurrentPrototypeId == Id(code));
        state.Content.TryGetCard(card.CurrentPrototypeId, out var definition);
        var spell = (SpellCardDefinition)definition!;
        return Run(state, [new(new(card.Id, card.CurrentPrototypeId, PlayerId.One, spell.TargetScope == SpellTargetScope.Global ? null : new(0), null), spell.Effects.Single(), null)]);
    }
    private static (MatchState State, ImmutableArray<FrameTransition> Frames) Invoke(MatchState state, MinionEntityState source, EffectTriggerKind trigger)
    {
        var card = state.CardInstances.Single(card => card.Id == source.CardInstanceId);
        state.Content.TryGetCard(card.CurrentPrototypeId, out var definition);
        return Run(state, [new(new(card.Id, card.CurrentPrototypeId, source.ControllerId, source.LaneId, source), definition!.Effects.Single(effect => effect.Trigger.Kind == trigger), null)]);
    }
    private static (MatchState State, ImmutableArray<FrameTransition> Frames) Apply(MatchState state, IEnumerable<AtomicIntent> intents)
    {
        var frame = FrameResolver.Resolve(state, intents);
        var result = Run(frame.State, EffectTriggers.FromEvents(state, frame));
        return (result.State, ImmutableArray.Create(frame).AddRange(result.Frames));
    }
    private static (MatchState State, ImmutableArray<FrameTransition> Frames) Run(MatchState state, ImmutableArray<EffectInvocation> roots)
    {
        ulong next = 1;
        ImmutableArray<EffectProgram> Programs(ImmutableArray<EffectInvocation> invocations) => invocations.Select(invocation => new EffectProgram(new(next++), invocation,
            new(invocation.Effect.Condition is { } condition ? new IfElseEffect(condition, invocation.Effect.Body, null) : invocation.Effect.Body, null, EffectResult.Empty))).ToImmutableArray();
        var ready = Programs(roots); var frames = ImmutableArray.CreateBuilder<FrameTransition>();
        while (!ready.IsEmpty)
        {
            Assert.True(frames.Count < 50, "Unexpected effect loop");
            var prepared = EffectEvaluator.PreparePrograms(FrameSnapshot.Create(state), ready, state.NextIntentId);
            var frame = FrameResolver.Resolve(state, prepared.Intents);
            Assert.Equal(MatchStatus.Active, frame.State.Status);
            Assert.DoesNotContain(frame.Receipts.Receipts, receipt => receipt.Status == IntentReceiptStatus.Error);
            ready = EffectEvaluator.AdvancePrograms(prepared, frame).AddRange(Programs(EffectTriggers.FromEvents(state, frame)));
            frames.Add(frame); state = frame.State;
        }
        return (state, frames.ToImmutable());
    }
}
