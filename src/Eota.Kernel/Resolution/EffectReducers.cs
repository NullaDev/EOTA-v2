using System.Collections.Immutable;
using System.Numerics;
using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;

namespace Eota.Kernel.Resolution;

internal sealed partial class ReductionContext
{
    private FieldEntityState FieldKeywords(IntentConflictGroup group, FieldEntityState field)
    {
        var keywords = field.Keywords.ToHashSet();
        foreach (var changes in group.Intents.OfType<ChangeFieldKeywordIntent>().GroupBy(value => value.Keyword).OrderBy(value => value.Key))
        {
            if (!Enum.IsDefined(changes.Key))
            {
                foreach (var invalid in changes) { AddReceipt(invalid.Id, IntentReceiptStatus.Rejected, "invalid-field-keyword", field.Id, null, null); }
                continue;
            }
            var remove = changes.Any(value => value.Remove);
            var existed = keywords.Contains(changes.Key);
            if (remove) { keywords.Remove(changes.Key); } else { keywords.Add(changes.Key); }
            foreach (var change in changes)
            {
                AddReceipt(change.Id, !change.Remove && remove ? IntentReceiptStatus.Rejected
                    : change.Remove == !existed ? IntentReceiptStatus.NoOp : IntentReceiptStatus.Applied,
                    !change.Remove && remove ? "keyword-remove-wins" : change.Remove ? "remove-keyword" : "add-keyword",
                    field.Id, null, null);
            }
        }
        var result = keywords.OrderBy(value => value).ToImmutableArray();
        if (!result.SequenceEqual(field.Keywords))
        { AddEvent(new EventDraft(DomainEventKind.FieldKeywordsChanged, field.Id, null, null, null, null, "field-keywords")); }
        return field with { Keywords = result };
    }

    private readonly ImmutableArray<NumericSetChoice> _setChoices;

    private void ValidateNumerics(IntentConflictGroup group)
    {
        foreach (var intent in group.Intents.OfType<NumericEffectIntent>())
        {
            if (!NumericRules.TryGetController(_state, intent, out _))
            { AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "invalid-numeric-target", null, null, null); }
        }
    }

    private BigInteger Number(IntentConflictGroup group, NumericProperty attribute, BigInteger original, BigInteger extraAdd)
    {
        var intents = group.Intents.OfType<NumericEffectIntent>()
            .Where(value => value.Attribute == attribute && !_receipts.ContainsKey(value.Id)).ToArray();
        var selected = _setChoices.SingleOrDefault(value => value.ConflictKey == group.Key && value.Attribute == attribute);
        var baseline = original;
        var contribution = NumericContribution.Add(extraAdd);
        foreach (var intent in intents)
        {
            if (intent.Operation == NumericOperation.Set && selected?.SelectedIntentId != intent.Id)
            { AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "set-not-selected", null, null, null); continue; }
            switch (intent.Operation)
            {
                case NumericOperation.Set: baseline = intent.Value; break;
                case NumericOperation.Add: contribution = contribution.Merge(NumericContribution.Add(intent.Value)); break;
                case NumericOperation.Multiply: contribution = contribution.Merge(NumericContribution.Multiply(intent.Value)); break;
                case NumericOperation.Divide when intent.Value != 0: contribution = contribution.Merge(NumericContribution.Divide(intent.Value)); break;
                default: SetFrameError("invalid-numeric-operation"); break;
            }
            var noOp = (intent.Operation == NumericOperation.Add && intent.Value == 0)
                || (intent.Operation is NumericOperation.Multiply or NumericOperation.Divide && intent.Value == 1);
            var entity = intent.Target.Type is EffectTargetType.Minion or EffectTargetType.Field ? new EntityId(intent.Target.Id) : (EntityId?)null;
            var player = intent.Target.Type == EffectTargetType.Hero ? new PlayerId((byte)intent.Target.Id) : (PlayerId?)null;
            AddReceipt(intent.Id, noOp ? IntentReceiptStatus.NoOp : IntentReceiptStatus.Applied,
                "modify-number", entity, player, intent.Value);
        }
        return contribution.Apply(baseline);
    }

    public void ReduceEffectDiagnostic(IntentConflictGroup group)
    {
        foreach (var intent in group.Intents)
        {
            if (intent is EffectDiagnosticIntent diagnostic)
            {
                if (diagnostic.IsError) { SetFrameError(diagnostic.Code); }
                AddReceipt(intent.Id, diagnostic.IsError ? IntentReceiptStatus.Error : IntentReceiptStatus.NoOp,
                    diagnostic.Code, null, null, null);
            }
            else { AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "wrong-intent-type", null, null, null); }
        }
    }

    public void ReducePlayerResource(IntentConflictGroup group)
    {
        ValidateNumerics(group);
        if (group.Key.StableTargetId > 1) { return; }
        var playerId = new PlayerId((byte)group.Key.StableTargetId);
        var index = IndexOfPlayer(_players, playerId);
        var player = _players[index];
        var maximum = BigInteger.Clamp(Number(group, NumericProperty.MaximumCost, player.MaxCost, 0), 0, _state.Protocol.Definition.MaxCostLimit);
        var current = BigInteger.Max(0, Number(group, NumericProperty.CurrentCost, player.CurrentCost,
            BigInteger.Max(0, maximum - player.MaxCost)));
        var temporary = BigInteger.Max(0, Number(group, NumericProperty.NextTurnCost, player.NextTurnCost, 0));
        if (!TryInt64(maximum, out var maxCost) || !TryInt64(current, out var cost) || !TryInt64(temporary, out var nextCost))
        { SetFrameError("arithmetic-overflow"); return; }
        _players = _players.SetItem(index, player with
        {
            MaxCost = maxCost,
            CurrentCost = cost,
            NextTurnCost = nextCost,
            AttachedEffects = ApplyAttachments(group, player.AttachedEffects, playerId, null)
        });
        if (maxCost != player.MaxCost || cost != player.CurrentCost || nextCost != player.NextTurnCost)
        { AddEvent(new EventDraft(DomainEventKind.PlayerResourcesChanged, null, playerId, null, player.MaxCost, maxCost, "player-resources")); }
    }

    private MinionEntityState Keywords(IntentConflictGroup group, MinionEntityState original, MinionEntityState updated)
    {
        var keywords = updated.Keywords.Where(value => value.Kind != MinionKeywordKind.Slow).ToDictionary(value => value.Kind, value => value);
        var slow = updated.SlowTurnsRemaining;
        foreach (var changes in group.Intents.OfType<ChangeMinionKeywordIntent>().GroupBy(value => value.Keyword).OrderBy(value => value.Key))
        {
            var valid = changes.Where(value => Enum.IsDefined(value.Keyword)
                && (value.Remove ? value.Parameter == 0 : value.Keyword == MinionKeywordKind.Slow ? value.Parameter > 0 : value.Parameter == 0)).ToArray();
            foreach (var invalid in changes.Except(valid))
            { AddReceipt(invalid.Id, IntentReceiptStatus.Rejected, "invalid-keyword-parameter", original.Id, null, null); }
            if (valid.Length == 0) { continue; }
            var remove = valid.Any(value => value.Remove);
            var existed = changes.Key == MinionKeywordKind.Slow ? slow > 0 : keywords.ContainsKey(changes.Key);
            if (changes.Key == MinionKeywordKind.Slow) { slow = remove ? 0 : Math.Max(slow, valid.Max(value => value.Parameter)); }
            else if (remove) { keywords.Remove(changes.Key); }
            else { keywords[changes.Key] = new MinionKeywordDefinition(changes.Key); }
            foreach (var change in valid)
            {
                var loses = !change.Remove && remove;
                AddReceipt(change.Id, loses ? IntentReceiptStatus.Rejected : change.Remove == !existed
                        && changes.Key != MinionKeywordKind.Slow ? IntentReceiptStatus.NoOp : IntentReceiptStatus.Applied,
                    loses ? "keyword-remove-wins" : change.Remove ? "remove-keyword" : "add-keyword", original.Id, null, change.Remove ? 0 : change.Parameter);
            }
        }
        if (slow > 0) { keywords[MinionKeywordKind.Slow] = new MinionKeywordDefinition(MinionKeywordKind.Slow, slow); }
        var generation = original.SlowGeneration;
        if (original.SlowTurnsRemaining == 0 && slow > 0)
        {
            if (generation == ulong.MaxValue) { SetFrameError("arithmetic-overflow"); }
            else { generation++; }
        }
        if (slow != updated.SlowTurnsRemaining)
        { AddEvent(new EventDraft(DomainEventKind.MinionSlowChanged, original.Id, null, null, original.SlowTurnsRemaining, slow, "slow-keyword")); }
        var result = keywords.Values.OrderBy(value => value.Kind).ToImmutableArray();
        if (group.Intents.Any(value => value is ChangeMinionKeywordIntent) && !original.Keywords.SequenceEqual(result))
        { AddEvent(new EventDraft(DomainEventKind.MinionKeywordsChanged, original.Id, null, null, null, null, "keywords")); }
        return updated with { Keywords = result, SlowTurnsRemaining = slow, SlowGeneration = generation };
    }

    private void ReduceEffectLaneResource(IntentConflictGroup group)
    {
        ValidateNumerics(group);
        var laneNumber = group.Key.StableTargetId / 2;
        var playerId = new PlayerId((byte)(group.Key.StableTargetId & 1));
        var lane = _lanes.SingleOrDefault(value => (ulong)value.Id.Value == laneNumber);
        if (lane is null)
        {
            foreach (var intent in group.Intents.Where(value => !_receipts.ContainsKey(value.Id)))
            { AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "lane-not-found", null, playerId, null); }
            return;
        }
        var own = GetPlayerLane(lane, playerId);
        var protection = own.PreventNextEtherDecay || group.Intents.Any(value => value is PreventEtherDecayIntent);
        var decays = group.Intents.OfType<DecayEtherIntent>().ToArray();
        var current = Number(group, NumericProperty.EtherActivation, own.EtherActivation, 0);
        current = BigInteger.Clamp(current, _state.Protocol.Definition.MinEtherActivation, _state.Protocol.Definition.MaxEtherActivation);
        foreach (var prevent in group.Intents.OfType<PreventEtherDecayIntent>())
        { AddReceipt(prevent.Id, own.PreventNextEtherDecay ? IntentReceiptStatus.NoOp : IntentReceiptStatus.Applied, "prevent-ether-decay", null, playerId, null); }
        var resets = group.Intents.OfType<ClearEtherIntent>().ToArray();
        foreach (var reset in resets)
        { AddReceipt(reset.Id, current == 0 ? IntentReceiptStatus.NoOp : IntentReceiptStatus.Applied, "clear-ether", null, playerId, (long)current); }
        if (resets.Length > 0) { current = 0; }
        var decay = AddCappedReceipts(decays.Select(value => (value.Id, Math.Max(0, value.Amount))),
            protection ? 0 : current - _state.Protocol.Definition.MinEtherActivation, protection ? "ether-decay-prevented" : "decay-ether", null, playerId);
        current -= decay;
        if (!TryInt64(current, out var value)) { SetFrameError("arithmetic-overflow"); return; }
        _lanes = OccupySlot(_lanes, lane, own with { EtherActivation = value, PreventNextEtherDecay = protection && decays.Length == 0 });
        if (protection && decays.Length > 0)
        { AddEvent(new EventDraft(DomainEventKind.EtherDecayPrevented, null, playerId, null, own.EtherActivation, value, "ether-decay-prevented", LaneId: lane.Id)); }
        if (value != own.EtherActivation)
        { AddEvent(new EventDraft(DomainEventKind.EtherActivationChanged, null, playerId, null, own.EtherActivation, value, "ether", LaneId: lane.Id)); }
    }
}
