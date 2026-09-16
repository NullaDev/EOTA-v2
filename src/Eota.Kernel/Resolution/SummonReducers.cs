using System.Collections.Immutable;
using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;

namespace Eota.Kernel.Resolution;

internal sealed partial class ReductionContext
{
    private void ReduceSummons(IntentConflictGroup group)
    {
        var valid = new List<SummonIntent>();
        foreach (var intent in group.Intents.OfType<SummonIntent>())
        {
            var lane = _state.Lanes.SingleOrDefault(value => value.Id == intent.LaneId);
            if (lane is null || !_state.Content.TryGetCard(intent.PrototypeId, out var definition)
                || (intent.SlotKind == BattlefieldSlotKind.Minion ? definition is not MinionCardDefinition
                    : intent.SlotKind != BattlefieldSlotKind.Field || definition is not FieldCardDefinition))
            { AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "invalid-summon", null, intent.ControllerId, null); continue; }
            var side = GetPlayerLane(lane, intent.ControllerId);
            if (LaneStatusRules.IsLocked(_state, lane.Id))
            { AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "lane-locked", null, intent.ControllerId, null); continue; }
            // A same-frame departure does not make a snapshot-occupied slot eligible.
            if ((intent.SlotKind == BattlefieldSlotKind.Minion ? side.MinionEntityId : side.FieldEntityId) is not null)
            { AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "summon-slot-occupied", null, intent.ControllerId, null); continue; }
            valid.Add(intent);
        }
        if (valid.Count == 0)
        {
            var remaining = group.Intents.Where(value => value is not SummonIntent).ToImmutableArray();
            if (!remaining.IsEmpty) { ReduceSlotOccupancy(group with { Intents = remaining }); }
            return;
        }
        var friendly = valid.Where(value => value.SourcePlayerId == value.ControllerId).ToArray();
        var preferred = friendly.Length > 0 ? friendly : valid.ToArray();
        var winnerId = Choose("summon-slot", group.Key.StableTargetId, preferred.OrderBy(value => value.Id.Value).Select(value => value.Id).ToImmutableArray());
        var winner = preferred.Single(value => value.Id == winnerId);
        foreach (var intent in group.Intents.Where(value => value.Id != winnerId && !_receipts.ContainsKey(value.Id)))
        { AddReceipt(intent.Id, IntentReceiptStatus.Rejected, intent is MoveMinionIntent ? "summon-wins" : "summon-not-selected", null, winner.ControllerId, null); }
        _state.Content.TryGetCard(winner.PrototypeId, out var prototype);
        var card = CreateCard(winner.PrototypeId, winner.ControllerId, CardZone.Battlefield);
        var id = new EntityId(checked(_nextEntityId++));
        var entity = CreateEntity(prototype!, id, card.Id, winner.ControllerId, winner.ControllerId, winner.LaneId);
        _entities.Add(id, entity);
        var currentLane = _lanes.Single(value => value.Id == winner.LaneId);
        var currentSide = GetPlayerLane(currentLane, winner.ControllerId);
        _lanes = OccupySlot(_lanes, currentLane, winner.SlotKind == BattlefieldSlotKind.Minion
            ? currentSide with { MinionEntityId = id } : currentSide with { FieldEntityId = id });
        AddReceipt(winnerId, IntentReceiptStatus.Applied, "summon", id, winner.ControllerId, null, card.Id);
        AddEvent(new EventDraft(DomainEventKind.EntityEntered, id, winner.ControllerId, null, null, null, "summon", card.Id, LaneId: winner.LaneId));
    }
}
