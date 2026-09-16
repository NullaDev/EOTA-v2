using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Eota.ContentCli;
using Eota.Kernel.Commands;
using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;
using Eota.Kernel.Resolution;
using Eota.Kernel.Rules;

namespace Eota.Content.Compiler.Tests;

public sealed class P8ContentTests
{
    private static readonly string Root = FindRoot();
    private static readonly ContentCatalog Catalog = ContentCatalog.Load(Root);
    private static readonly CardPrototypeId Friend = new("TEST-P8-FRIEND"), Other = new("TEST-P8-OTHER"), Field = new("TEST-P8-FIELD");
    private static readonly RuleContentPack Rules = RuleContentPack.Create("eota.card/v2", Catalog.Bundle.Rules.Cards.Concat<CardDefinition>([
        new MinionCardDefinition(Friend, CardSource.Test, Profession.Guardian, 0, 1, 20, [new(MinionKeywordKind.Guard)]) { Tags = ["beast", "mechanical", "firearm"] },
        new MinionCardDefinition(Other, CardSource.Test, Profession.Neutral, 0, 2, 20, []),
        new FieldCardDefinition(Field, CardSource.Test, Profession.Neutral, 0, new FiniteFieldLifetimeDefinition(3), false)]));

    public static IEnumerable<object[]> Cards() => Catalog.Bundle.Rules.Cards.Select(value => new object[] { value.Id.Value });
    public static IEnumerable<object[]> Scenarios()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "tests", "Fixtures", "P8Content", "scenarios.json")));
        return json.RootElement.EnumerateArray().Select(value => new object[] { value.GetProperty("id").GetString()!, value.GetRawText() }).ToArray();
    }

    [Theory]
    [MemberData(nameof(Cards))]
    public void EveryCardCanBePlannedAndResolveThreeTurns(string prototype)
    {
        var state = Create(opening: Rules.Cards.Length);
        var card = state.CardInstances.Single(value => value.OwnerId == PlayerId.One && value.CurrentPrototypeId.Value == prototype);
        Rules.TryGetCard(card.CurrentPrototypeId, out var definition);
        AuthoritativeCommand command = definition is SpellCardDefinition spell
            ? new PlanSpellCommand(PlayerId.One, 0, card.Id, spell.TargetScope == SpellTargetScope.Global ? null : new LaneId(0))
            : new PlanCardCommand(PlayerId.One, 0, card.Id, new LaneId(0));
        var accepted = MatchCommandProcessor.Accept(state, command);
        Assert.True(accepted.IsAccepted, accepted.Receipt.RejectionReason.ToString());
        state = accepted.State;
        for (var index = 0; index < 3 && state.Status == MatchStatus.Active; index++)
        {
            foreach (var player in state.Players)
            { state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(player.Id, player.CommandRevision)).State; }
            var result = TurnResolver.ResolveReadyTurn(state);
            Assert.NotEqual(MatchStatus.Failed, result.State.Status);
            Assert.DoesNotContain(result.Frames.SelectMany(value => value.Receipts.Receipts), value => value.Status == IntentReceiptStatus.Error);
            state = result.State;
        }
        Assert.DoesNotContain(state.CardInstances, value => value.Id == card.Id && value.Zone == CardZone.Hand);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void AuthoredEffectMatchesIndependentObservableScenario(string scenarioId, string scenarioJson)
    {
        using var json = JsonDocument.Parse(scenarioJson);
        var scenario = json.RootElement; var setup = scenario.GetProperty("setup");
        long Option(string key, long fallback = 0) => setup.TryGetProperty(key, out var value) ? value.GetInt64() : fallback;
        var state = Create(handLimit: (int)Option("handLimit", 200));
        var prototype = new CardPrototypeId(scenario.GetProperty("cardId").GetString()!);
        Rules.TryGetCard(prototype, out var definition);
        var friendlyPrototype = Option("friendlySoldier") == 1 ? new CardPrototypeId("EOTA-CORE-GUA-MIN-001") : Option("nonMechanical") == 1 ? Other : Friend;
        if (definition is MinionCardDefinition) { state = Summon(state, prototype, PlayerId.One, 0); }
        else if (Option("emptyFriendly") == 0) { state = Summon(state, friendlyPrototype, PlayerId.One, 0); }
        state = Summon(state, definition is FieldCardDefinition ? prototype : Field, PlayerId.One, 0, BattlefieldSlotKind.Field);
        state = Summon(state, Field, PlayerId.Two, 0, BattlefieldSlotKind.Field);
        if (Option("emptyEnemy") == 0) { state = Summon(state, Other, PlayerId.Two, 0); }
        if (scenarioId.StartsWith("GUA-MIN-015", StringComparison.Ordinal) || Option("adjacentFriendly") == 1) { state = Summon(state, Friend, PlayerId.One, 1); }
        if (prototype.Value == "EOTA-CORE-ARC-SPL-006") { state = Summon(state, Other, PlayerId.Two, 1); }
        state = state with
        {
            Players = state.Players.SetItem(0, state.Players[0] with { HeroHealth = Option("heroHealthBefore", 90) }),
            Entities = state.Entities.Select(entity => entity is MinionEntityState minion ? minion with
            {
                CurrentHealth = minion.ControllerId == PlayerId.Two ? Option("enemyHealthBefore", 20)
                    : minion.Id == state.Lanes[0].PlayerOne.MinionEntityId && definition is not MinionCardDefinition ? Option("friendlyHealthBefore", 10) : minion.CurrentHealth,
                SlowTurnsRemaining = minion.ControllerId == PlayerId.One ? Option("friendlySlow", minion.SlowTurnsRemaining) : minion.SlowTurnsRemaining,
                Keywords = minion.ControllerId == PlayerId.One && Option("friendlySlow") > 0
                    ? minion.Keywords.Where(keyword => keyword.Kind != MinionKeywordKind.Slow).Append(new MinionKeywordDefinition(MinionKeywordKind.Slow, Option("friendlySlow"))).ToImmutableArray()
                    : minion.ControllerId == PlayerId.One && Option("skirmisher") == 1 ? minion.Keywords.Add(new(MinionKeywordKind.Skirmisher)) : minion.Keywords
            } : entity).ToImmutableArray(),
            Lanes = state.Lanes.SetItem(0, state.Lanes[0] with
            {
                PlayerOne = state.Lanes[0].PlayerOne with { EtherActivation = Option("etherBefore", 3) },
                PlayerTwo = state.Lanes[0].PlayerTwo with { EtherActivation = Option("enemyEtherBefore", 0) }
            })
        };
        if (Option("emptyDeck") == 1)
        {
            var deck = state.Players[0].Deck;
            state = state with
            {
                Players = state.Players.SetItem(0, state.Players[0] with { Deck = [] }),
                CardInstances = state.CardInstances.Select(value => deck.Contains(value.Id) ? value with { Zone = CardZone.Removed } : value).ToImmutableArray()
            };
        }
        for (var i = 0; i < Option("prefill"); i++)
        { state = FrameResolver.Resolve(state, [new HandCardIntent(state.NextIntentId, PlayerId.One, HandRequestKind.Generate, new(0, "arrange", i), new("EOTA-TOKEN-GUA-MIN-001"))]).State; }
        var sourceEntity = state.Entities.SingleOrDefault(value => value.ControllerId == PlayerId.One && state.CardInstances.Single(card => card.Id == value.CardInstanceId).CurrentPrototypeId == prototype);
        var sourceCard = sourceEntity?.CardInstanceId ?? state.CardInstances.First(value => value.OwnerId == PlayerId.One && value.CurrentPrototypeId == prototype).Id;
        var trigger = Enum.Parse<EffectTriggerKind>(scenario.GetProperty("trigger").GetString()!, true);
        var effect = Assert.Single(definition!.Effects.Where(value => value.Trigger.Kind == trigger));
        var eventEntity = trigger is EffectTriggerKind.FriendlyEntered or EffectTriggerKind.FriendlyCombat or EffectTriggerKind.FriendlyDied
            ? state.Entities.SingleOrDefault(value => value.Id == state.Lanes[0].PlayerOne.MinionEntityId)
            : trigger is EffectTriggerKind.SelfDied or EffectTriggerKind.SelfDamaged or EffectTriggerKind.CombatDamage or EffectTriggerKind.MinionCombat
                ? sourceEntity : state.Entities.SingleOrDefault(value => value.Id == state.Lanes[0].PlayerTwo.MinionEntityId);
        var enemyId = state.Lanes[0].PlayerTwo.MinionEntityId;
        if (Option("death") == 1 || Option("friendlyDeath") == 1)
        {
            var dead = Option("death") == 1 ? sourceEntity! : eventEntity!;
            AtomicIntent death = dead is MinionEntityState ? new KillMinionIntent(state.NextIntentId, dead.Id) : new DestroyFieldIntent(state.NextIntentId, dead.Id);
            state = FrameResolver.Resolve(state, [death]).State;
        }
        var replaced = trigger == EffectTriggerKind.ReplacementEntered ? new EntityTombstone(new(999), new(999), new(999), Friend,
            PlayerId.One, PlayerId.One, new(0), EntityRemovalReason.Replace, 4, 10, 20, null, state.NextFrameId,
            new MinionEntityState(new(999), new(999), PlayerId.One, PlayerId.One, new(0), 4, 10, 20, [], 0, 0, false)) : null;
        var fact = new DomainEvent(new(999), DomainEventKind.CombatDeclared, state.NextFrameId, eventEntity?.Id, eventEntity?.ControllerId,
            null, 0, 4, "scenario", eventEntity?.CardInstanceId, enemyId, new(0));
        var invocation = new EffectInvocation(new(sourceCard, prototype, PlayerId.One, definition is SpellCardDefinition { TargetScope: SpellTargetScope.Global } ? null : new LaneId(0), sourceEntity),
            effect, fact, eventEntity, replaced, eventEntity is null ? null : state.CardInstances.Single(value => value.Id == eventEntity.CardInstanceId).CurrentPrototypeId);
        EffectNode body = effect.Condition is { } condition ? new IfElseEffect(condition, effect.Body, null) : effect.Body;
        ImmutableArray<EffectProgram> programs = [new(new(1), invocation, new(body, null, EffectResult.Empty))];
        for (var frame = 0; !programs.IsEmpty; frame++)
        {
            Assert.True(frame < 50, scenarioId);
            var prepared = EffectEvaluator.PreparePrograms(FrameSnapshot.Create(state), programs, state.NextIntentId);
            var transition = FrameResolver.Resolve(state, prepared.Intents);
            Assert.DoesNotContain(transition.Receipts.Receipts, value => value.Status == IntentReceiptStatus.Error);
            Assert.Equal(MatchStatus.Active, transition.State.Status);
            programs = EffectEvaluator.AdvancePrograms(prepared, transition); state = transition.State;
        }
        foreach (var check in scenario.GetProperty("expect").EnumerateObject())
        { Assert.True(check.Value.ToString() == Metric(state, sourceEntity?.Id, check.Name), $"{scenarioId} {check.Name}: expected {check.Value}, got {Metric(state, sourceEntity?.Id, check.Name)}"); }
    }

    private static string Metric(MatchState state, EntityId? source, string metric)
    {
        var p = state.Players[0];
        MinionEntityState? Minion(PlayerId owner, int lane = 0) => state.Entities.OfType<MinionEntityState>().SingleOrDefault(value => value.ControllerId == owner && value.LaneId.Value == lane);
        var f = Minion(PlayerId.One); var e = Minion(PlayerId.Two); var s = state.Entities.OfType<MinionEntityState>().SingleOrDefault(value => value.Id == source);
        var field = state.Entities.OfType<FieldEntityState>().SingleOrDefault(value => value.ControllerId == PlayerId.One && value.LaneId.Value == 0);
        var hand = p.Hand.Select(id => state.CardInstances.Single(value => value.Id == id)).ToArray();
        CardDefinition Def(CardInstanceState card) => CardInstanceRules.Definition(state, card);
        CardDefinition Base(CardInstanceState card) { state.Content.TryGetCard(card.CurrentPrototypeId, out var value); return value!; }
        long Flag(bool value) => value ? 1 : 0;
        if (metric.StartsWith("handHasTag:", StringComparison.Ordinal)) { return Flag(Def(hand[0]).Tags.Contains(metric[11..])).ToString(CultureInfo.InvariantCulture); }
        if (metric.StartsWith("keyword:", StringComparison.Ordinal)) { return Flag(f!.Keywords.Any(value => value.Kind == Enum.Parse<MinionKeywordKind>(metric[8..], true))).ToString(CultureInfo.InvariantCulture); }
        if (metric.StartsWith("handKeyword:", StringComparison.Ordinal)) { return Flag(hand.Select(Def).OfType<MinionCardDefinition>().Any(card => card.Keywords.Any(value => value.Kind == Enum.Parse<MinionKeywordKind>(metric[12..], true)))).ToString(CultureInfo.InvariantCulture); }
        object value = metric switch
        {
            "hand" => hand.Length,
            "zeroCostHand" => hand.Count(card => Def(card).Cost == 0),
            "handKind" => Def(hand[0]).Kind,
            "handPrototype" => hand[0].CurrentPrototypeId.Value,
            "handProfession" => Def(hand[0]).Profession,
            "handTag" => string.Join(",", Def(hand[0]).Tags),
            "handAttack" => ((MinionCardDefinition)Def(hand[0])).Attack,
            "handMaxHealth" => ((MinionCardDefinition)Def(hand[0])).Health,
            "handAttackDelta" => ((MinionCardDefinition)Def(hand[0])).Attack - ((MinionCardDefinition)Base(hand[0])).Attack,
            "handMaxHealthDelta" => ((MinionCardDefinition)Def(hand[0])).Health - ((MinionCardDefinition)Base(hand[0])).Health,
            "handCostDelta" => Def(hand[0]).Cost - Base(hand[0]).Cost,
            "mechanicalDeckAttackDelta" => p.Deck.Select(id => state.CardInstances.Single(c => c.Id == id)).Where(c => Base(c) is MinionCardDefinition && Base(c).Tags.Contains("mechanical")).Min(c => ((MinionCardDefinition)Def(c)).Attack - ((MinionCardDefinition)Base(c)).Attack),
            "selfAttack" => s!.Attack,
            "selfHealth" => s!.CurrentHealth,
            "selfMaxHealth" => s!.MaximumHealth,
            "friendlyAttack" => f!.Attack,
            "friendlyHealth" => f!.CurrentHealth,
            "friendlySlow" => f!.SlowTurnsRemaining,
            "friendlyMaxHealth" => f!.MaximumHealth,
            "enemyAttack" => e!.Attack,
            "enemyHealth" => e!.CurrentHealth,
            "enemySlow" => e!.SlowTurnsRemaining,
            "adjacentAttack" => Minion(PlayerId.One, 1)!.Attack,
            "adjacentMaxHealth" => Minion(PlayerId.One, 1)!.MaximumHealth,
            "adjacentGuard" => Flag(Minion(PlayerId.One, 1)!.Keywords.Any(keyword => keyword.Kind == MinionKeywordKind.Guard)),
            "adjacentEnemyHealth" => Minion(PlayerId.Two, 1)!.CurrentHealth,
            "friendlyPrototype" => state.CardInstances.Single(c => c.Id == f!.CardInstanceId).CurrentPrototypeId.Value,
            "enemyCount" => state.Entities.OfType<MinionEntityState>().Count(c => c.ControllerId == PlayerId.Two),
            "friendlyCount" => state.Entities.OfType<MinionEntityState>().Count(c => c.ControllerId == PlayerId.One),
            "friendlyDeaths" => state.Tombstones.Count(value => value.ControllerId == PlayerId.One && value.Reason == EntityRemovalReason.Death),
            "friendlyFields" => state.Entities.OfType<FieldEntityState>().Count(c => c.ControllerId == PlayerId.One),
            "enemyFields" => state.Entities.OfType<FieldEntityState>().Count(c => c.ControllerId == PlayerId.Two),
            "enemyFieldEnergy" => ((FiniteFieldLifetimeState)state.Entities.OfType<FieldEntityState>().Single(value => value.ControllerId == PlayerId.Two).Lifetime).Energy,
            "friendlyEnergy" => ((FiniteFieldLifetimeState)field!.Lifetime).Energy,
            "friendlyAttached" => f!.AttachedEffects.Length,
            "enemyAttached" => e!.AttachedEffects.Length,
            "playerAttached" => p.AttachedEffects.Length,
            "enemyPlayerAttached" => state.Players[1].AttachedEffects.Length,
            "enemyPoisonDuration" => state.Players[1].AttachedEffects.Single().RemainingDuration!,
            "chargeRequirement" => f!.ChargeRequirementOverride ?? -1,
            "friendlyDamageAdjustment" => f!.IncomingDamageAdjustment,
            "enemyDamageAdjustment" => e!.IncomingDamageAdjustment,
            "heroHealth" => p.HeroHealth,
            "enemyHeroHealth" => state.Players[1].HeroHealth,
            "maxCost" => p.MaxCost,
            "nextCost" => p.NextTurnCost,
            "ether" => state.Lanes[0].PlayerOne.EtherActivation,
            "enemyEther" => state.Lanes[0].PlayerTwo.EtherActivation,
            "activatedLanes" => state.Lanes.Count(l => l.PlayerOne.EtherActivation > 0),
            "decayProtected" => Flag(state.Lanes[0].PlayerOne.PreventNextEtherDecay),
            _ => throw new InvalidOperationException(metric)
        };
        return Convert.ToString(value, CultureInfo.InvariantCulture)!;
    }

    private static MatchState Create(int opening = 0, int handLimit = 260)
    {
        var protocol = CompiledGameProtocol.Compile(GameProtocolDefinition.DefaultV0 with
        {
            RequiredDeckSize = Rules.Cards.Length,
            OpeningHandSize = opening,
            HandLimit = handLimit,
            InitialPlayerCost = 100,
            InitialMaxCost = 100,
            MaxCostLimit = 200,
            InitialHeroHealth = 100,
            CardsDrawnPerTurn = 0,
            DeckConstructionPolicy = DeckConstructionPolicy.DevelopmentAnySource
        });
        var deck = DeckDefinition.Create(Profession.Neutral, Rules.Cards.Select(card => new DeckEntry(card.Id, 1)));
        var state = MatchFactory.Create(new(protocol, Rules, 123456789, deck, deck)).State!;
        return state with { Players = state.Players.SetItem(0, state.Players[0] with { HeroHealth = 90 }) };
    }
    private static MatchState Summon(MatchState state, CardPrototypeId id, PlayerId owner, int lane, BattlefieldSlotKind kind = BattlefieldSlotKind.Minion)
        => FrameResolver.Resolve(state, [new SummonIntent(state.NextIntentId, owner, owner, new(lane), id, kind)]).State;
    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eota.sln"))) { dir = dir.Parent; }
        return dir?.FullName ?? throw new InvalidOperationException();
    }
}
