using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Tests;

public sealed partial class FrameResolverTests
{
    [Fact]
    public void TemporaryChargeSharesSetPriorityAndRestoresPermanentChangesUnderOverlappingLayers()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 1, 3, 3);
        var target = new EffectTarget(EffectTargetType.Minion, state.Entities[0].Id.Value);
        AtomicIntent[] intents = [
            new NumericEffectIntent(new IntentId(1), target, PlayerId.Two, NumericProperty.ChargeRequirement, NumericOperation.Set, 99),
            Grant(2, PlayerId.One, 2, 2) ];
        var first = FrameResolver.Resolve(state, intents);
        Assert.Equal(2, first.State.Entities[0].ChargeRequirementOverride);
        Assert.Equal(state.RuleRng, first.State.RuleRng);
        Assert.Equal(IntentReceiptStatus.Rejected, first.Receipts.Receipts[0].Status);
        Assert.Equal(first.AfterStateHash, FrameResolver.Resolve(state, intents.Reverse()).AfterStateHash);
        var second = FrameResolver.Resolve(first.State, [Grant(3, PlayerId.One, 5, 1)]);
        Assert.Equal(5, second.State.Entities[0].ChargeRequirementOverride);
        var permanent = FrameResolver.Resolve(second.State, [new NumericEffectIntent(new IntentId(4), target, PlayerId.One,
            NumericProperty.ChargeRequirement, NumericOperation.Set, 7)]);
        Assert.Equal(5, permanent.State.Entities[0].ChargeRequirementOverride);
        var decay = FrameResolver.Resolve(permanent.State, [new DecayModifiersIntent(new IntentId(5), target, 1)]);
        Assert.Equal(2, decay.State.Entities[0].ChargeRequirementOverride);
        var expired = FrameResolver.Resolve(decay.State, [new DecayModifiersIntent(new IntentId(6), target, 1)]);
        Assert.Equal(7, expired.State.Entities[0].ChargeRequirementOverride);
        Assert.Empty(expired.State.Entities[0].TemporaryChargeOverrides);

        TemporaryModifierIntent Grant(ulong id, PlayerId source, long amount, long duration) => new(new IntentId(id), target,
            new TemporaryModifier(new IntentId(id), source, EffectAction.ModifyNumber, NumericProperty.ChargeRequirement,
                NumericOperation.Set, amount, MinionKeywordKind.Swift, duration));
    }
}
