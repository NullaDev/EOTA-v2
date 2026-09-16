using System.Collections.Immutable;
using Eota.Kernel.Content;

namespace Eota.Kernel.Effects;

public enum EffectTriggerKind
{
    SelfEntered, FriendlyEntered, EnemyEntered, SelfSpellCast, FriendlySpellCast, SelfDamaged, Combat, Attack, TurnEnd,
    SelfDied, SelfLeft, FriendlyDied, EnemyDied, FriendlyLeft, EnemyLeft, FriendlyCombat, EnemyCombat, EnemyAttack, ReplacementEntered,
    PreCombatCharge, EndTurnCharge, CombatDamage, EntryStage, CombatStage, MinionCombat
}
public enum SelectionScope { Lane, All, Adjacent, OtherLanes }
public enum SelectorKind
{
    Self, FriendlyHero, EnemyHero, FriendlyMinions, EnemyMinions, AllMinions, FriendlyFields, EnemyFields,
    FriendlyLanes, EnemyLanes, EventSubject, Targets, PreviousAffected, PreviousCreated, PreviousRemoved,
    FriendlyHand, EnemyHand, FriendlyDeck, EnemyDeck, EventTarget
}
public enum EffectTargetType { Hero, Minion, Field, Lane, Card }
public enum NumericProperty { Attack, MaximumHealth, DamageTaken, FieldEnergy, MaximumCost, CurrentCost, EtherActivation, NextTurnCost, CardCost, ChargeRequirement, StoredCharge, IncomingDamageAdjustment, BaseAttack }
public enum NumericOperation { Set, Add, Multiply, Divide }
public enum EffectAction { Damage, Heal, Kill, ModifyNumber, AddKeyword, RemoveKeyword, PreventEtherDecay, ClearEther, LoseHealth, ReduceSlow }
public enum ArithmeticOperation { Add, Subtract, Multiply, Divide }
public enum ComparisonOperation { Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual }

public sealed record EffectTrigger(EffectTriggerKind Kind, SelectionScope Scope = SelectionScope.Lane,
    EffectTargetType? SubjectType = null, bool OtherOnly = false);
public sealed record EffectSelector(SelectorKind Kind, SelectionScope Scope = SelectionScope.Lane, EffectTargetType? FilterType = null, CardFilter? Filter = null);
public readonly record struct EffectTarget(EffectTargetType Type, ulong Id);
public sealed record EffectDefinition(string Id, EffectTrigger Trigger, EffectCondition? Condition, EffectNode Body, long? ChargeRequirement = null);

public abstract record IntExpression;
public sealed record ConstantExpression(long Value) : IntExpression;
public sealed record VariableExpression(string Name) : IntExpression;
public sealed record BinaryExpression(ArithmeticOperation Operation, IntExpression Left, IntExpression Right) : IntExpression;

public abstract record EffectCondition;
public sealed record CompareCondition(IntExpression Left, ComparisonOperation Operation, IntExpression Right) : EffectCondition;
public sealed record ExistsCondition(EffectSelector Selector) : EffectCondition;
public sealed record KeywordCondition(EffectSelector Selector, MinionKeywordKind Keyword) : EffectCondition;
public enum EffectCardReference { Source, Event, Replaced, Target }
public sealed record CardMatchesCondition(EffectCardReference Subject, CardFilter Filter) : EffectCondition;
public sealed record AllCondition(ImmutableArray<EffectCondition> Children) : EffectCondition;
public sealed record NotCondition(EffectCondition Child) : EffectCondition;

public abstract record EffectNode;
public sealed record ParallelEffect(ImmutableArray<EffectNode> Children) : EffectNode;
public sealed record IfElseEffect(EffectCondition Condition, EffectNode Then, EffectNode? Else) : EffectNode;
public sealed record RetargetEffect(EffectSelector Selector, EffectNode Body) : EffectNode;
public sealed record LifecycleEffect(LifecycleOperation Operation, EffectSelector Selector, Eota.Kernel.Primitives.CardPrototypeId? PrototypeId) : EffectNode;
public sealed record EmitEffect(
    EffectAction Action,
    EffectSelector Selector,
    IntExpression? Amount = null,
    NumericProperty Attribute = NumericProperty.Attack,
    NumericOperation Operation = NumericOperation.Add,
    MinionKeywordKind Keyword = MinionKeywordKind.Swift,
    IntExpression? Duration = null) : EffectNode;
