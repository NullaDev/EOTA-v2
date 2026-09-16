using System.Collections.Immutable;
using Eota.Kernel.Effects;
using Eota.Kernel.Primitives;

namespace Eota.Kernel.Content;

public static class CardRuleScope
{
    public static ImmutableArray<CardPrototypeId> Closure(RuleContentPack content, IEnumerable<CardPrototypeId> roots)
    {
        var seen = new HashSet<CardPrototypeId>();
        var pending = new Queue<CardPrototypeId>(roots);
        while (pending.TryDequeue(out var id))
        {
            if (!seen.Add(id)) { continue; }
            if (!content.TryGetCard(id, out var card)) { throw new ArgumentException($"Unknown card '{id}'.", nameof(roots)); }
            foreach (var child in card!.Effects.SelectMany(effect => References(effect.Body))) { pending.Enqueue(child); }
        }
        return [.. seen.Order()];
    }

    private static IEnumerable<CardPrototypeId> References(EffectNode node)
    {
        switch (node)
        {
            case SummonEffect summon: yield return summon.PrototypeId; break;
            case HandEffect { PrototypeId: { } id }: yield return id; break;
            case LifecycleEffect { PrototypeId: { } id }: yield return id; break;
        }
        IEnumerable<EffectNode> children = node switch
        {
            ParallelEffect parallel => parallel.Children,
            SequenceEffect sequence => sequence.Steps,
            IfElseEffect branch => branch.Else is null ? [branch.Then] : [branch.Then, branch.Else],
            RetargetEffect target => [target.Body],
            LoopEffect loop => [loop.Body],
            GrantEffect grant => [grant.Definition.Body],
            _ => []
        };
        foreach (var child in children)
        { foreach (var id in References(child)) { yield return id; } }
    }
}
