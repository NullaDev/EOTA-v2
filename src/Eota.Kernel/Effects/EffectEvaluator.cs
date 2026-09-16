using System.Collections.Immutable;
using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;
using Eota.Kernel.Rules;

namespace Eota.Kernel.Effects;

public sealed record EffectSource(CardInstanceId CardId, CardPrototypeId PrototypeId, PlayerId ControllerId,
    LaneId? LaneId, BattlefieldEntityState? FrozenEntity);
public sealed record EffectInvocation(EffectSource Source, EffectDefinition Effect, DomainEvent? Event,
    BattlefieldEntityState? EventEntity = null, EntityTombstone? Replaced = null, CardPrototypeId? EventPrototypeId = null);

public sealed record EffectBatchWorkItem(WorkItemId Id, FrameId FrameId, IntentId FirstIntentId,
    ImmutableArray<EffectInvocation> Invocations) : WorkItem(Id, FrameId)
{
    public override ImmutableArray<AtomicIntent> Evaluate(FrameSnapshot snapshot) => EffectEvaluator.Evaluate(snapshot, Invocations, FirstIntentId);
}

public sealed record EffectFailureWorkItem(WorkItemId Id, FrameId FrameId, IntentId IntentId, string Code) : WorkItem(Id, FrameId)
{
    public override ImmutableArray<AtomicIntent> Evaluate(FrameSnapshot snapshot) => [new EffectDiagnosticIntent(IntentId, Code, true)];
}

public static partial class EffectEvaluator
{
    public static ImmutableArray<AtomicIntent> Evaluate(FrameSnapshot snapshot, ImmutableArray<EffectInvocation> invocations, IntentId firstIntentId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var evaluator = new Evaluation(snapshot.State, firstIntentId.Value);
        try
        {
            foreach (var invocation in invocations.OrderBy(value => value.Event?.Id.Value ?? 0)
                         .ThenBy(value => value.Source.CardId.Value).ThenBy(value => value.Effect.Id, StringComparer.Ordinal))
            {
                evaluator.Invocation = invocation;
                if (invocation.Effect.Condition is null || evaluator.Condition(invocation.Effect.Condition, null))
                { evaluator.Node(invocation.Effect.Body, null, 0); }
                else { evaluator.Add(id => new EffectDiagnosticIntent(id, "condition-false", false)); }
            }
        }
        catch (EffectEvaluationException error) { evaluator.Error(error.Message); }
        catch (ArithmeticException) { evaluator.Error("expression-arithmetic-error"); }
        return evaluator.Intents.ToImmutable();
    }

    private sealed class EffectEvaluationException(string code) : Exception(code);

    private sealed class Evaluation(MatchState state, ulong firstId)
    {
        private ulong _nextId = firstId;
        private int _steps;
        public EffectInvocation Invocation { get; set; } = null!;
        public EffectResult Previous { get; set; } = EffectResult.Empty;
        public int LoopIndex { get; set; }
        public ImmutableArray<AtomicIntent>.Builder Intents { get; } = ImmutableArray.CreateBuilder<AtomicIntent>();

        public void Add(Func<IntentId, AtomicIntent> create)
        {
            if (Intents.Count >= state.Protocol.Definition.MaxEffectIntentsPerFrame) { throw new EffectEvaluationException("effect-intent-budget-exceeded"); }
            Intents.Add(create(new IntentId(checked(_nextId++))));
        }
        public void Error(string code) => Intents.Add(new EffectDiagnosticIntent(new IntentId(checked(_nextId++)), code, true));
        private void Step()
        {
            if (++_steps > state.Protocol.Definition.MaxEffectIntentsPerFrame * 32L) { throw new EffectEvaluationException("effect-evaluation-budget-exceeded"); }
        }

        public void Node(EffectNode node, EffectTarget? target, int depth)
        {
            Step();
            if (depth > 32) { throw new EffectEvaluationException("effect-depth-exceeded"); }
            switch (node)
            {
                case EffectErrorNode error:
                    throw new EffectEvaluationException(error.Code);
                case GrantEffect grant:
                    var attachmentTargets = Select(grant.Selector, target);
                    var lifetime = grant.Duration is null ? (long?)null : Expression(grant.Duration, target);
                    if (attachmentTargets.IsEmpty || lifetime <= 0)
                    { Add(id => new EffectDiagnosticIntent(id, "empty-attachment", false)); break; }
                    foreach (var selected in attachmentTargets)
                    { Add(id => new AttachEffectIntent(id, selected, grant.Definition, lifetime, Invocation.Source)); }
                    break;
                case ParallelEffect parallel:
                    foreach (var child in parallel.Children) { Node(child, target, depth + 1); }
                    break;
                case IfElseEffect branch:
                    if (Condition(branch.Condition, target)) { Node(branch.Then, target, depth + 1); }
                    else if (branch.Else is not null) { Node(branch.Else, target, depth + 1); }
                    else { Add(id => new EffectDiagnosticIntent(id, "condition-false", false)); }
                    break;
                case RetargetEffect retarget:
                    var targets = Select(retarget.Selector, target);
                    if (targets.IsEmpty) { Add(id => new EffectDiagnosticIntent(id, "empty-target", false)); }
                    foreach (var selected in targets) { Node(retarget.Body, selected, depth + 1); }
                    break;
                case EmitEffect emit:
                    var selectedTargets = Select(emit.Selector, target);
                    if (selectedTargets.IsEmpty) { Add(id => new EffectDiagnosticIntent(id, "empty-target", false)); break; }
                    var amount = emit.Amount is null ? 0 : Expression(emit.Amount, target);
                    var duration = emit.Duration is null ? (long?)null : Expression(emit.Duration, target);
                    foreach (var selected in selectedTargets)
                    {
                        if (duration is { } turns)
                        {
                            if (turns <= 0) { Add(id => new EffectDiagnosticIntent(id, "empty-duration", false)); }
                            else
                            {
                                Add(id => new TemporaryModifierIntent(id, selected,
                                new TemporaryModifier(id, Invocation.Source.ControllerId, emit.Action, emit.Attribute, emit.Operation, amount, emit.Keyword, turns)));
                            }
                        }
                        else { Emit(emit, selected, amount); }
                    }
                    break;
                case LaneStatusEffect status:
                    var statusTargets = Select(status.Selector, target).GroupBy(value => value.Id / 2).Select(group => group.First()).ToArray();
                    var statusDuration = status.Duration is null ? (long?)null : Expression(status.Duration, target);
                    if (statusTargets.Length == 0) { Add(id => new EffectDiagnosticIntent(id, "empty-target", false)); }
                    foreach (var selected in statusTargets)
                    {
                        if (selected.Type != EffectTargetType.Lane) { throw new EffectEvaluationException("effect-target-type-mismatch"); }
                        if (statusDuration is <= 0) { Add(id => new EffectDiagnosticIntent(id, "empty-duration", false)); }
                        else { Add(id => new ChangeLaneStatusIntent(id, new LaneId((int)(selected.Id / 2)), Invocation.Source.ControllerId, status.Status, status.Remove, statusDuration)); }
                    }
                    break;
                case LifecycleEffect lifecycle:
                    var lifecycleTargets = Select(lifecycle.Selector, target);
                    if (lifecycleTargets.IsEmpty) { Add(id => new EffectDiagnosticIntent(id, "empty-target", false)); }
                    foreach (var selected in lifecycleTargets)
                    { Add(id => new LifecycleEffectIntent(id, selected, Invocation.Source.ControllerId, lifecycle.Operation, lifecycle.PrototypeId)); }
                    break;
                case SummonEffect summon:
                    var slots = Select(summon.Selector, target);
                    if (slots.IsEmpty) { Add(id => new EffectDiagnosticIntent(id, "empty-target", false)); }
                    foreach (var slot in slots)
                    {
                        if (slot.Type != EffectTargetType.Lane) { throw new EffectEvaluationException("effect-target-type-mismatch"); }
                        Add(id => new SummonIntent(id, Invocation.Source.ControllerId, new PlayerId((byte)(slot.Id & 1)),
                            new LaneId((int)(slot.Id / 2)), summon.PrototypeId, summon.SlotKind));
                    }
                    break;
                case HandEffect hand:
                    var count = Expression(hand.Count, target);
                    if (count <= 0) { Add(id => new EffectDiagnosticIntent(id, "empty-hand-request", false)); break; }
                    foreach (var player in Select(hand.Selector, target))
                    {
                        if (player.Type != EffectTargetType.Hero) { throw new EffectEvaluationException("effect-target-type-mismatch"); }
                        for (long index = 0; index < count; index++)
                        {
                            var key = new DrawAllocationKey(Invocation.Source.CardId.Value, Invocation.Effect.Id, Intents.Count);
                            Add(id => new HandCardIntent(id, new PlayerId((byte)player.Id), hand.Kind, key, hand.PrototypeId, hand.Filter));
                        }
                    }
                    break;
                default: throw new EffectEvaluationException("unsupported-effect-node");
            }
        }

        private void Emit(EmitEffect emit, EffectTarget target, long amount)
        {
            if (emit.Action == EffectAction.AddKeyword && emit.Keyword == MinionKeywordKind.Slow && amount <= 0)
            { throw new EffectEvaluationException("invalid-slow-count"); }
            Add(id => emit.Action switch
            {
                EffectAction.Damage when target.Type == EffectTargetType.Hero => new DamageHeroIntent(id, new PlayerId((byte)target.Id), amount),
                EffectAction.Damage when target.Type == EffectTargetType.Minion => new DamageMinionIntent(id, new EntityId(target.Id), amount),
                EffectAction.LoseHealth when target.Type == EffectTargetType.Minion => new LoseMinionHealthIntent(id, new EntityId(target.Id), amount),
                EffectAction.ReduceSlow when target.Type == EffectTargetType.Minion => new DecayMinionSlowIntent(id, new EntityId(target.Id), amount),
                EffectAction.Heal when target.Type == EffectTargetType.Hero => new HealHeroIntent(id, new PlayerId((byte)target.Id), amount),
                EffectAction.Heal when target.Type == EffectTargetType.Minion => new HealMinionIntent(id, new EntityId(target.Id), amount),
                EffectAction.Kill when target.Type == EffectTargetType.Hero => new KillHeroIntent(id, new PlayerId((byte)target.Id)),
                EffectAction.Kill when target.Type == EffectTargetType.Minion => new KillMinionIntent(id, new EntityId(target.Id)),
                EffectAction.Kill when target.Type == EffectTargetType.Field => new DestroyFieldIntent(id, new EntityId(target.Id)),
                EffectAction.ModifyNumber => new NumericEffectIntent(id, target, Invocation.Source.ControllerId, emit.Attribute, emit.Operation, amount),
                EffectAction.AddKeyword when target.Type == EffectTargetType.Minion => new ChangeMinionKeywordIntent(id, new EntityId(target.Id), emit.Keyword, false, amount),
                EffectAction.RemoveKeyword when target.Type == EffectTargetType.Minion => new ChangeMinionKeywordIntent(id, new EntityId(target.Id), emit.Keyword, true, 0),
                EffectAction.AddKeyword when target.Type == EffectTargetType.Field => new ChangeFieldKeywordIntent(id, new EntityId(target.Id), FieldKeyword(emit.Keyword), false),
                EffectAction.RemoveKeyword when target.Type == EffectTargetType.Field => new ChangeFieldKeywordIntent(id, new EntityId(target.Id), FieldKeyword(emit.Keyword), true),
                EffectAction.AddKeyword when target.Type == EffectTargetType.Card => new ChangeCardKeywordIntent(id, new CardInstanceId(target.Id), emit.Keyword, false, amount),
                EffectAction.RemoveKeyword when target.Type == EffectTargetType.Card => new ChangeCardKeywordIntent(id, new CardInstanceId(target.Id), emit.Keyword, true, 0),
                EffectAction.PreventEtherDecay when target.Type == EffectTargetType.Lane => new PreventEtherDecayIntent(id, new PlayerId((byte)(target.Id & 1)), new LaneId((int)(target.Id / 2))),
                EffectAction.ClearEther when target.Type == EffectTargetType.Lane => new ClearEtherIntent(id, new PlayerId((byte)(target.Id & 1)), new LaneId((int)(target.Id / 2))),
                _ => new EffectDiagnosticIntent(id, "effect-target-type-mismatch", true)
            });
        }

        public ImmutableArray<EffectTarget> Select(EffectSelector selector, EffectTarget? target)
        {
            var selected = SelectUnfiltered(selector, target);
            if (selector.Filter is null) { return selected; }
            return selected.Where(value =>
            {
                var cardId = value.Type == EffectTargetType.Card ? value.Id : state.Entities.SingleOrDefault(entity => entity.Id.Value == value.Id)?.CardInstanceId.Value;
                var instance = state.CardInstances.SingleOrDefault(card => card.Id.Value == cardId);
                return instance is not null && CardInstanceRules.Matches(CardInstanceRules.Definition(state, instance), selector.Filter);
            }).ToImmutableArray();
        }

        private ImmutableArray<EffectTarget> SelectUnfiltered(EffectSelector selector, EffectTarget? target)
        {
            Step();
            var source = Invocation.Source;
            if (selector.Kind is SelectorKind.PreviousAffected or SelectorKind.PreviousCreated or SelectorKind.PreviousRemoved)
            {
                var previous = selector.Kind == SelectorKind.PreviousAffected ? Previous.Affected
                    : selector.Kind == SelectorKind.PreviousCreated ? Previous.Created : Previous.Removed;
                if (selector.FilterType is { } filter) { previous = previous.Where(value => value.Type == filter).ToImmutableArray(); }
                return selector.Kind == SelectorKind.PreviousRemoved ? previous : previous.Where(value => value.Type switch
                {
                    EffectTargetType.Minion or EffectTargetType.Field => state.Entities.Any(entity => entity.Id.Value == value.Id),
                    EffectTargetType.Card => state.CardInstances.Any(card => card.Id.Value == value.Id && card.Zone is CardZone.Hand or CardZone.Deck),
                    _ => true
                }).ToImmutableArray();
            }
            if (selector.Kind == SelectorKind.Targets) { return target.HasValue ? [target.Value] : []; }
            if (selector.Kind == SelectorKind.FriendlyHero) { return [new EffectTarget(EffectTargetType.Hero, source.ControllerId.Value)]; }
            if (selector.Kind == SelectorKind.EnemyHero) { return [new EffectTarget(EffectTargetType.Hero, source.ControllerId.Opponent.Value)]; }
            if (selector.Kind is SelectorKind.FriendlyHand or SelectorKind.EnemyHand or SelectorKind.FriendlyDeck or SelectorKind.EnemyDeck)
            {
                var player = state.Players.Single(value => value.Id == (selector.Kind is SelectorKind.FriendlyHand or SelectorKind.FriendlyDeck ? source.ControllerId : source.ControllerId.Opponent));
                return (selector.Kind is SelectorKind.FriendlyHand or SelectorKind.EnemyHand ? player.Hand : player.Deck)
                    .OrderBy(value => value.Value).Select(value => new EffectTarget(EffectTargetType.Card, value.Value)).ToImmutableArray();
            }
            if (selector.Kind == SelectorKind.Self)
            {
                var entity = state.Entities.SingleOrDefault(value => value.Id == source.FrozenEntity?.Id);
                return entity is null ? [] : [EntityTarget(entity)];
            }
            if (selector.Kind == SelectorKind.EventSubject)
            {
                var entity = state.Entities.SingleOrDefault(value => value.Id == Invocation.Event?.EntityId);
                return entity is null ? [] : [EntityTarget(entity)];
            }
            if (selector.Kind == SelectorKind.EventTarget)
            {
                var entity = state.Entities.SingleOrDefault(value => value.Id == Invocation.Event?.TargetEntityId);
                return entity is null ? [] : [EntityTarget(entity)];
            }
            IEnumerable<EffectTarget> targets;
            if (selector.Kind is SelectorKind.FriendlyLanes or SelectorKind.EnemyLanes)
            {
                var controller = selector.Kind == SelectorKind.FriendlyLanes ? source.ControllerId : source.ControllerId.Opponent;
                targets = state.Lanes.Where(lane => InScope(lane.Id))
                    .Select(lane => new EffectTarget(EffectTargetType.Lane, ((ulong)(uint)lane.Id.Value << 1) | controller.Value));
            }
            else
            {
                var minions = selector.Kind is SelectorKind.FriendlyMinions or SelectorKind.EnemyMinions or SelectorKind.AllMinions;
                var friendly = selector.Kind is SelectorKind.FriendlyMinions or SelectorKind.FriendlyFields;
                targets = state.Entities.Where(entity => (minions ? entity is MinionEntityState : entity is FieldEntityState)
                        && (selector.Kind == SelectorKind.AllMinions || entity.ControllerId == (friendly ? source.ControllerId : source.ControllerId.Opponent))
                        && InScope(entity.LaneId)).Select(EntityTarget);
            }
            var ordered = targets.OrderBy(value => value.Type).ThenBy(value => value.Id)
                .Take(state.Protocol.Definition.MaxEffectIntentsPerFrame + 1).ToImmutableArray();
            if (ordered.Length > state.Protocol.Definition.MaxEffectIntentsPerFrame) { throw new EffectEvaluationException("effect-target-budget-exceeded"); }
            return ordered;
            bool InScope(LaneId lane) => selector.Scope == SelectionScope.All || (selector.Scope == SelectionScope.OtherLanes ? lane != source.LaneId : selector.Scope == SelectionScope.Adjacent
                ? source.LaneId is { } origin && Math.Abs((long)lane.Value - origin.Value) == 1 : lane == source.LaneId);
        }

        private static EffectTarget EntityTarget(BattlefieldEntityState entity) => new(entity is MinionEntityState ? EffectTargetType.Minion : EffectTargetType.Field, entity.Id.Value);

        public bool Condition(EffectCondition condition, EffectTarget? target)
        {
            Step();
            return condition switch
            {
                CompareCondition compare => Compare(Expression(compare.Left, target), compare.Operation, Expression(compare.Right, target)),
                ExistsCondition exists => !Select(exists.Selector, target).IsEmpty,
                KeywordCondition keyword => Select(keyword.Selector, target).Any(selected => HasKeyword(selected, keyword.Keyword)),
                CardMatchesCondition matches => CardMatches(matches, target),
                AllCondition all => all.Children.All(child => Condition(child, target)),
                NotCondition not => !Condition(not.Child, target),
                _ => throw new EffectEvaluationException("unsupported-condition")
            };
        }

        private bool HasKeyword(EffectTarget selected, MinionKeywordKind keyword)
        {
            if (selected.Type == EffectTargetType.Card)
            {
                var instance = state.CardInstances.Single(value => value.Id.Value == selected.Id);
                return CardInstanceRules.Definition(state, instance) is MinionCardDefinition card && card.Keywords.Any(value => value.Kind == keyword);
            }
            return state.Entities.Any(entity => entity.Id.Value == selected.Id && (entity is MinionEntityState minion
                ? minion.Keywords.Any(value => value.Kind == keyword)
                : entity is FieldEntityState field && keyword is MinionKeywordKind.Replace or MinionKeywordKind.Replaceable && field.Keywords.Contains(FieldKeyword(keyword))));
        }

        private bool CardMatches(CardMatchesCondition condition, EffectTarget? target)
        {
            var prototype = condition.Subject switch
            {
                EffectCardReference.Source => Invocation.Source.PrototypeId,
                EffectCardReference.Event => Invocation.EventPrototypeId ?? Prototype(Invocation.EventEntity?.CardInstanceId ?? Invocation.Event?.CardInstanceId),
                EffectCardReference.Replaced => Invocation.Replaced?.PrototypeId,
                EffectCardReference.Target when target is { Type: EffectTargetType.Card } card => Prototype(new CardInstanceId(card.Id)),
                EffectCardReference.Target when target is { } entity => Prototype(state.Entities.SingleOrDefault(value => value.Id.Value == entity.Id)?.CardInstanceId),
                _ => null
            };
            return prototype is { } id && state.Content.TryGetCard(id, out var definition) && CardInstanceRules.Matches(definition!, condition.Filter);
        }

        private CardPrototypeId? Prototype(CardInstanceId? id) => state.CardInstances.SingleOrDefault(value => value.Id == id)?.CurrentPrototypeId;

        public long Expression(IntExpression expression, EffectTarget? target)
        {
            Step();
            return expression switch
            {
                ConstantExpression constant => constant.Value,
                VariableExpression variable => Variable(variable.Name, target),
                BinaryExpression binary => Calculate(Expression(binary.Left, target), binary.Operation, Expression(binary.Right, target)),
                _ => throw new EffectEvaluationException("unsupported-expression")
            };
        }

        private long Variable(string name, EffectTarget? target)
        {
            var source = Invocation.Source;
            if (name == "previous.scalar") { return Previous.Scalar; }
            if (name == "previous.affectedCount") { return Previous.Affected.Length; }
            if (name == "previous.createdCount") { return Previous.Created.Length; }
            if (name == "previous.removedCount") { return Previous.Removed.Length; }
            if (name == "loop.index") { return LoopIndex; }
            var owner = state.Players.Single(value => value.Id == source.ControllerId);
            if (name == "owner.health") { return owner.HeroHealth; }
            if (name == "owner.maxHealth") { return owner.HeroMaximumHealth; }
            if (name == "owner.cost") { return owner.CurrentCost; }
            if (name == "owner.maxCost") { return owner.MaxCost; }
            if (name == "owner.resonatingLanes") { return state.Lanes.Count(value => (source.ControllerId == PlayerId.One ? value.PlayerOne : value.PlayerTwo).EtherActivation >= 1); }
            if (name == "event.amount") { return Invocation.Event?.CurrentValue ?? throw new EffectEvaluationException("unavailable-event-amount"); }
            if (name.StartsWith("event.", StringComparison.Ordinal)) { return EntityValue(Invocation.EventEntity, name[6..]); }
            if (name.StartsWith("replaced.", StringComparison.Ordinal)) { return EntityValue(Invocation.Replaced?.FinalEntity, name[9..]); }
            if (name == "lane.ether")
            {
                var lane = state.Lanes.SingleOrDefault(value => value.Id == source.LaneId) ?? throw new EffectEvaluationException("missing-source-lane");
                return (source.ControllerId == PlayerId.One ? lane.PlayerOne : lane.PlayerTwo).EtherActivation;
            }
            if (name.StartsWith("source.", StringComparison.Ordinal))
            {
                var entity = state.Entities.SingleOrDefault(value => value.Id == source.FrozenEntity?.Id) ?? source.FrozenEntity;
                return EntityValue(entity, name[7..]);
            }
            if (name.StartsWith("target.", StringComparison.Ordinal) && target is { } selected)
            {
                var property = name[7..];
                if (selected.Type == EffectTargetType.Card)
                {
                    var instance = state.CardInstances.Single(value => value.Id.Value == selected.Id);
                    var definition = CardInstanceRules.Definition(state, instance);
                    return property switch
                    {
                        "cost" => definition.Cost,
                        "attack" when definition is MinionCardDefinition minion => minion.Attack,
                        "health" or "maxHealth" when definition is MinionCardDefinition minion => minion.Health,
                        _ => throw new EffectEvaluationException("unavailable-card-property")
                    };
                }
                if (selected.Type == EffectTargetType.Hero)
                {
                    var player = state.Players.Single(value => value.Id.Value == selected.Id);
                    return property == "health" ? player.HeroHealth : player.HeroMaximumHealth;
                }
                if (selected.Type == EffectTargetType.Lane)
                {
                    var lane = state.Lanes.Single(value => (ulong)value.Id.Value == selected.Id / 2);
                    return (selected.Id % 2 == 0 ? lane.PlayerOne : lane.PlayerTwo).EtherActivation;
                }
                return EntityValue(state.Entities.SingleOrDefault(value => value.Id.Value == selected.Id), property);
            }
            throw new EffectEvaluationException("invalid-expression-variable");
        }
        private static long EntityValue(BattlefieldEntityState? entity, string property) => (entity, property) switch
        {
            (MinionEntityState minion, "attack") => minion.Attack,
            (MinionEntityState minion, "health") => minion.CurrentHealth,
            (MinionEntityState minion, "maxHealth") => minion.MaximumHealth,
            (MinionEntityState minion, "slow") => minion.SlowTurnsRemaining,
            (FieldEntityState { Lifetime: FiniteFieldLifetimeState finite }, "energy") => finite.Energy,
            _ => throw new EffectEvaluationException("unavailable-expression-property")
        };
        private static FieldKeywordKind FieldKeyword(MinionKeywordKind kind) => kind switch
        {
            MinionKeywordKind.Replace => FieldKeywordKind.Replace,
            MinionKeywordKind.Replaceable => FieldKeywordKind.Replaceable,
            _ => throw new EffectEvaluationException("invalid-field-keyword")
        };
        private static bool Compare(long left, ComparisonOperation operation, long right) => operation switch
        {
            ComparisonOperation.Equal => left == right,
            ComparisonOperation.NotEqual => left != right,
            ComparisonOperation.Less => left < right,
            ComparisonOperation.LessOrEqual => left <= right,
            ComparisonOperation.Greater => left > right,
            ComparisonOperation.GreaterOrEqual => left >= right,
            _ => throw new EffectEvaluationException("invalid-comparison")
        };
        private static long Calculate(long left, ArithmeticOperation operation, long right) => operation switch
        {
            ArithmeticOperation.Add => checked(left + right),
            ArithmeticOperation.Subtract => checked(left - right),
            ArithmeticOperation.Multiply => checked(left * right),
            ArithmeticOperation.Divide => checked(left / right),
            _ => throw new EffectEvaluationException("invalid-arithmetic-operation")
        };
    }
}
