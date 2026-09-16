using System.Collections.Immutable;
using Eota.Kernel.Effects;

namespace Eota.Client.Desktop;

public sealed partial class DesktopCatalog
{
    private ImmutableDictionary<string, ImmutableArray<CardPresentation>> _related = ImmutableDictionary<string, ImmutableArray<CardPresentation>>.Empty;

    public ImmutableArray<CardPresentation> RelatedCards(string prototypeId) => _related.GetValueOrDefault(prototypeId, []);

    private void BuildRelatedCards()
    {
        var references = _rules.Cards.ToDictionary(card => card.Id.Value,
            card => card.Effects.SelectMany(effect => ReferencedCards(effect.Body)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        _related = Cards.ToImmutableDictionary(card => card.Id, card =>
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { card.Id };
            var pending = new Queue<string>(references[card.Id]);
            while (pending.TryDequeue(out var id))
            {
                if (!seen.Add(id)) { continue; }
                if (references.TryGetValue(id, out var children)) { foreach (var child in children) { pending.Enqueue(child); } }
            }
            return Cards.Where(value => value.Source == "Token" && value.Id != card.Id && seen.Contains(value.Id)).ToImmutableArray();
        }, StringComparer.Ordinal);
    }

    private static IEnumerable<string> ReferencedCards(EffectNode node)
    {
        switch (node)
        {
            case SummonEffect summon: yield return summon.PrototypeId.Value; break;
            case HandEffect { PrototypeId: { } id }: yield return id.Value; break;
            case LifecycleEffect { PrototypeId: { } id }: yield return id.Value; break;
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
        { foreach (var id in ReferencedCards(child)) { yield return id; } }
    }
}
