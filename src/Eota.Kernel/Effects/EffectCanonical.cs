using Eota.Kernel.Determinism;

namespace Eota.Kernel.Effects;

public static class EffectCanonical
{
    public static void Write(CanonicalWriter writer, EffectDefinition effect)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(effect);
        writer.WriteString(effect.Id);
        writer.WriteInt32((int)effect.Trigger.Kind);
        writer.WriteInt32((int)effect.Trigger.Scope);
        writer.WriteBoolean(effect.Trigger.SubjectType.HasValue);
        if (effect.Trigger.SubjectType is { } subjectType) { writer.WriteInt32((int)subjectType); }
        writer.WriteBoolean(effect.Trigger.OtherOnly);
        writer.WriteBoolean(effect.ChargeRequirement.HasValue);
        if (effect.ChargeRequirement is { } charge) { writer.WriteInt64(charge); }
        writer.WriteBoolean(effect.Condition is not null);
        if (effect.Condition is not null) { Condition(writer, effect.Condition); }
        Node(writer, effect.Body);
    }

    private static void Selector(CanonicalWriter writer, EffectSelector selector)
    {
        writer.WriteInt32((int)selector.Kind);
        writer.WriteInt32((int)selector.Scope);
        writer.WriteBoolean(selector.FilterType.HasValue);
        if (selector.FilterType is { } type) { writer.WriteInt32((int)type); }
        Filter(writer, selector.Filter);
    }

    private static void Filter(CanonicalWriter writer, CardFilter? filter)
    {
        writer.WriteBoolean(filter is not null);
        if (filter is null) { return; }
        writer.WriteBoolean(filter.Kind.HasValue);
        if (filter.Kind is { } kind) { writer.WriteInt32((int)kind); }
        writer.WriteBoolean(filter.Profession.HasValue);
        if (filter.Profession is { } profession) { writer.WriteInt32((int)profession); }
        writer.WriteBoolean(filter.Tag is not null);
        if (filter.Tag is { } tag) { writer.WriteString(tag); }
    }

    private static void Expression(CanonicalWriter writer, IntExpression expression)
    {
        switch (expression)
        {
            case ConstantExpression constant: writer.WriteInt32(1); writer.WriteInt64(constant.Value); break;
            case VariableExpression variable: writer.WriteInt32(2); writer.WriteString(variable.Name); break;
            case BinaryExpression binary:
                writer.WriteInt32(3); writer.WriteInt32((int)binary.Operation);
                Expression(writer, binary.Left); Expression(writer, binary.Right); break;
            default: throw new ArgumentException("Unknown expression.", nameof(expression));
        }
    }

    private static void Condition(CanonicalWriter writer, EffectCondition condition)
    {
        switch (condition)
        {
            case CompareCondition compare:
                writer.WriteInt32(1); writer.WriteInt32((int)compare.Operation);
                Expression(writer, compare.Left); Expression(writer, compare.Right); break;
            case ExistsCondition exists: writer.WriteInt32(2); Selector(writer, exists.Selector); break;
            case KeywordCondition keyword:
                writer.WriteInt32(3); Selector(writer, keyword.Selector); writer.WriteInt32((int)keyword.Keyword); break;
            case AllCondition all:
                writer.WriteInt32(4); writer.WriteCount(all.Children.Length);
                foreach (var child in all.Children) { Condition(writer, child); }
                break;
            case NotCondition not: writer.WriteInt32(5); Condition(writer, not.Child); break;
            case CardMatchesCondition matches:
                writer.WriteInt32(6); writer.WriteInt32((int)matches.Subject); Filter(writer, matches.Filter); break;
            default: throw new ArgumentException("Unknown condition.", nameof(condition));
        }
    }

    internal static void Node(CanonicalWriter writer, EffectNode node)
    {
        switch (node)
        {
            case ParallelEffect parallel:
                writer.WriteInt32(1); writer.WriteCount(parallel.Children.Length);
                foreach (var child in parallel.Children) { Node(writer, child); }
                break;
            case IfElseEffect branch:
                writer.WriteInt32(2); Condition(writer, branch.Condition); Node(writer, branch.Then);
                writer.WriteBoolean(branch.Else is not null);
                if (branch.Else is not null) { Node(writer, branch.Else); }
                break;
            case RetargetEffect retarget: writer.WriteInt32(3); Selector(writer, retarget.Selector); Node(writer, retarget.Body); break;
            case EmitEffect emit:
                writer.WriteInt32(4); writer.WriteInt32((int)emit.Action); Selector(writer, emit.Selector);
                writer.WriteBoolean(emit.Amount is not null);
                if (emit.Amount is not null) { Expression(writer, emit.Amount); }
                writer.WriteInt32((int)emit.Attribute); writer.WriteInt32((int)emit.Operation); writer.WriteInt32((int)emit.Keyword);
                writer.WriteBoolean(emit.Duration is not null);
                if (emit.Duration is { } duration) { Expression(writer, duration); }
                break;
            case LifecycleEffect lifecycle:
                writer.WriteInt32(5); writer.WriteInt32((int)lifecycle.Operation); Selector(writer, lifecycle.Selector);
                writer.WriteBoolean(lifecycle.PrototypeId.HasValue);
                if (lifecycle.PrototypeId is { } prototype) { writer.WriteString(prototype.Value); }
                break;
            case SequenceEffect sequence:
                writer.WriteInt32(6); writer.WriteCount(sequence.Steps.Length);
                foreach (var step in sequence.Steps) { Node(writer, step); }
                break;
            case LoopEffect loop:
                writer.WriteInt32(7); Expression(writer, loop.Count); Node(writer, loop.Body);
                writer.WriteBoolean(loop.While is not null);
                if (loop.While is not null) { Condition(writer, loop.While); }
                break;
            case SummonEffect summon:
                writer.WriteInt32(8); Selector(writer, summon.Selector); writer.WriteString(summon.PrototypeId.Value);
                writer.WriteInt32((int)summon.SlotKind); break;
            case EffectErrorNode error:
                writer.WriteInt32(9); writer.WriteString(error.Code); break;
            case HandEffect hand:
                writer.WriteInt32(10); writer.WriteInt32((int)hand.Kind); Selector(writer, hand.Selector); Expression(writer, hand.Count);
                writer.WriteBoolean(hand.PrototypeId.HasValue);
                if (hand.PrototypeId is { } generated) { writer.WriteString(generated.Value); }
                Filter(writer, hand.Filter); break;
            case GrantEffect grant:
                writer.WriteInt32(11); Selector(writer, grant.Selector); Write(writer, grant.Definition);
                writer.WriteBoolean(grant.Duration is not null);
                if (grant.Duration is { } lifetime) { Expression(writer, lifetime); }
                break;
            case LaneStatusEffect status:
                writer.WriteInt32(12); Selector(writer, status.Selector); writer.WriteInt32((int)status.Status);
                writer.WriteBoolean(status.Remove); writer.WriteBoolean(status.Duration is not null);
                if (status.Duration is { } statusDuration) { Expression(writer, statusDuration); }
                break;
            default: throw new ArgumentException("Unknown effect node.", nameof(node));
        }
    }
}
