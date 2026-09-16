using System.Collections.Immutable;
using Eota.Client.AI;
using Eota.Kernel.Content;
using Eota.Kernel.Effects;

namespace Eota.Client.Desktop;

public sealed record DesktopAiSettings(string Difficulty = "Normal", ulong Seed = 104729, string PolicyVersion = AiPolicy.Version);
public sealed record DesktopAiDifficulty(string Id, string Name, string Description);

public sealed partial class DesktopCatalog
{
    public static ImmutableArray<DesktopAiDifficulty> AiDifficulties { get; } =
    [
        new("Easy", "简单", "保留起手牌，在合法位置随机出牌，适合熟悉规则。"),
        new("Normal", "普通", "调整起手牌，按战场威胁、攻防与卡牌效果选择行动。"),
        new("Hard", "困难", "在普通策略基础上，比较最多三张牌的费用与位置组合。")
    ];
    public ImmutableArray<AiCardProfile> AiCards { get; }

    private static ImmutableArray<AiCardProfile> ProjectAiCards(RuleContentPack rules) => rules.Cards.Select(card =>
    {
        var hints = ImmutableArray.CreateBuilder<AiEffectHint>();
        foreach (var effect in card.Effects)
        {
            var immediate = effect.Trigger.Kind is EffectTriggerKind.SelfEntered or EffectTriggerKind.SelfSpellCast;
            Visit(effect.Body, (immediate ? 1 : 0.55) * (effect.Condition is null ? 1 : 0.6), null, hints);
        }
        return new AiCardProfile(card.Id.Value, hints.ToImmutable(), card is FieldCardDefinition { PreventsActiveAttacksInLane: true },
            card is FieldCardDefinition field ? field.Lifetime is FiniteFieldLifetimeDefinition finite ? finite.InitialEnergy : 4 : 0);
    }).ToImmutableArray();

    private static void Visit(EffectNode node, double weight, EffectSelector? retarget, ImmutableArray<AiEffectHint>.Builder hints)
    {
        void Add(string action, EffectSelector selector, double amount, string attribute = "", string operation = "Add")
        {
            if (selector.Kind == SelectorKind.Targets && retarget is not null) { selector = retarget; }
            hints.Add(new AiEffectHint(action, selector.Kind.ToString(), selector.Scope.ToString(), amount, attribute, operation, weight));
        }
        switch (node)
        {
            case EmitEffect emit:
                Add(emit.Action.ToString(), emit.Selector, Estimate(emit.Amount), emit.Attribute.ToString(), emit.Operation.ToString()); break;
            case HandEffect hand: Add("Draw", hand.Selector, Estimate(hand.Count)); break;
            case SummonEffect summon: Add("Summon", summon.Selector, 1); break;
            case LifecycleEffect lifecycle: Add(lifecycle.Operation.ToString(), lifecycle.Selector, 1); break;
            case LaneStatusEffect status: Add(status.Remove ? "RemoveLaneStatus" : "LaneStatus", status.Selector, 1); break;
            case ParallelEffect parallel:
                foreach (var child in parallel.Children) { Visit(child, weight, retarget, hints); }
                break;
            case SequenceEffect sequence:
                foreach (var child in sequence.Steps) { Visit(child, weight, retarget, hints); }
                break;
            case IfElseEffect branch:
                Visit(branch.Then, weight * 0.5, retarget, hints);
                if (branch.Else is not null) { Visit(branch.Else, weight * 0.5, retarget, hints); }
                break;
            case RetargetEffect target: Visit(target.Body, weight, target.Selector, hints); break;
            case LoopEffect loop: Visit(loop.Body, weight * Math.Clamp(Estimate(loop.Count), 0, 4), retarget, hints); break;
            case GrantEffect grant: Visit(grant.Definition.Body, weight * 0.4, grant.Selector, hints); break;
        }
    }

    private static double Estimate(IntExpression? expression) => expression switch
    {
        ConstantExpression constant => constant.Value,
        BinaryExpression binary => Math.Clamp(binary.Operation switch
        {
            ArithmeticOperation.Add => Estimate(binary.Left) + Estimate(binary.Right),
            ArithmeticOperation.Subtract => Estimate(binary.Left) - Estimate(binary.Right),
            ArithmeticOperation.Multiply => Estimate(binary.Left) * Estimate(binary.Right),
            ArithmeticOperation.Divide => Estimate(binary.Left) / Math.Max(1, Estimate(binary.Right)),
            _ => 1
        }, -100, 100),
        _ => 1 // Runtime variables remain unknown; never evaluate against authoritative match state.
    };
}
