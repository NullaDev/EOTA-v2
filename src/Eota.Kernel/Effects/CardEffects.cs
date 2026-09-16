using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Effects;

public sealed record CardFilter(CardKind? Kind = null, Profession? Profession = null, string? Tag = null);
public enum HandRequestKind { Generate, Draw }
public sealed record HandEffect(HandRequestKind Kind, EffectSelector Selector, IntExpression Count,
    CardPrototypeId? PrototypeId = null, CardFilter? Filter = null) : EffectNode;
public readonly record struct DrawAllocationKey(ulong SourceCardId, string EffectId, int Ordinal) : IComparable<DrawAllocationKey>
{
    public static bool operator <(DrawAllocationKey left, DrawAllocationKey right) => left.CompareTo(right) < 0;
    public static bool operator <=(DrawAllocationKey left, DrawAllocationKey right) => left.CompareTo(right) <= 0;
    public static bool operator >(DrawAllocationKey left, DrawAllocationKey right) => left.CompareTo(right) > 0;
    public static bool operator >=(DrawAllocationKey left, DrawAllocationKey right) => left.CompareTo(right) >= 0;
    public int CompareTo(DrawAllocationKey other)
    {
        var source = SourceCardId.CompareTo(other.SourceCardId);
        if (source != 0) { return source; }
        var effect = StringComparer.Ordinal.Compare(EffectId, other.EffectId);
        return effect != 0 ? effect : Ordinal.CompareTo(other.Ordinal);
    }
}
public sealed record HandCardIntent(IntentId Id, PlayerId PlayerId, HandRequestKind Kind, DrawAllocationKey AllocationKey,
    CardPrototypeId? PrototypeId = null, CardFilter? Filter = null) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.HandCapacity, PlayerId.Value);
}
public sealed record ChangeCardKeywordIntent(IntentId Id, CardInstanceId CardId, MinionKeywordKind Keyword, bool Remove, long Parameter) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.CardZone, CardId.Value);
}

public static class CardInstanceRules
{
    public static CardDefinition Definition(MatchState state, CardInstanceState instance)
    {
        state.Content.TryGetCard(instance.CurrentPrototypeId, out var definition);
        return definition switch
        {
            MinionCardDefinition minion => minion with
            {
                Cost = instance.Cost ?? minion.Cost,
                Attack = instance.Attack ?? minion.Attack,
                Health = instance.MaximumHealth ?? minion.Health,
                Keywords = instance.Keywords.IsDefault ? minion.Keywords : instance.Keywords
            },
            FieldCardDefinition field => field with { Cost = instance.Cost ?? field.Cost },
            SpellCardDefinition spell => spell with { Cost = instance.Cost ?? spell.Cost },
            _ => throw new InvalidOperationException("Card prototype missing.")
        };
    }
    public static bool Matches(CardDefinition card, CardFilter? filter) => filter is null
        || ((filter.Kind is null || filter.Kind == card.Kind) && (filter.Profession is null || filter.Profession == card.Profession)
            && (filter.Tag is null || card.Tags.Contains(filter.Tag, StringComparer.Ordinal)));
}
