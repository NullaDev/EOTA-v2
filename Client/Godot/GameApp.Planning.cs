using System.Collections.Immutable;
using Eota.Transport.Contracts;

namespace Eota.GodotClient;

public partial class GameApp
{
    private readonly Dictionary<ulong, CardTile> _plannedTiles = [];
    private readonly Dictionary<int, SpellPlans> _laneSpellPlans = [];
    private SpellPlans _globalSpellPlans = null!;

    private void RenderPlanning(ObserverView view)
    {
        foreach (var tile in _plannedTiles.Values) { tile.GetParent().RemoveChild(tile); tile.QueueFree(); }
        _plannedTiles.Clear();
        foreach (var tile in _registry.Values) { tile.Show(); }
        var plans = view is { Stage: "Planning", Status: "Active", Private: { } own } ? own.Planning : [];
        var canCancel = view.Private is { } observer && view.Status == "Active" && view.Stage == "Planning"
            && !view.Players.Single(player => player.PlayerId == observer.PlayerId).Submitted;
        foreach (var plan in plans.Where(plan => plan.Card.CardKind is "Minion" or "Field"))
        {
            if (plan.LaneId is not { } lane) { continue; }
            var slot = _slots[(lane, view.Private!.PlayerId, plan.Card.CardKind)];
            // Keep the authoritative entity registered underneath a replacement preview. Cancelling
            // reveals it again; preview nodes never enter the live entity/animation registry.
            foreach (var current in slot.GetChildren().OfType<CardTile>()) { current.Hide(); }
            var tile = Tile(plan.Card, true); slot.AddChild(tile); tile.Highlight(true);
            tile.Clicked = _ => CancelPlan(plan.PlanCommandId);
            tile.TooltipText += "\n待入场 · 点击撤回";
            _plannedTiles.Add(plan.PlanCommandId, tile);
        }
        foreach (var slot in _slots.Values)
        { slot.GetNode<Godot.Label>("Placeholder").Visible = !slot.GetChildren().OfType<CardTile>().Any(tile => tile.Visible); }
        foreach (var (lane, markers) in _laneSpellPlans)
        { markers.Bind(plans.Where(plan => plan.Card.CardKind == "Spell" && plan.LaneId == lane).ToImmutableArray(), canCancel); }
        _globalSpellPlans.Bind(plans.Where(plan => plan.Card.CardKind == "Spell" && plan.LaneId is null).ToImmutableArray(), canCancel);
    }

    private void CancelPlan(ulong id)
    {
        if (_session is not { } session || Animating || _frames.Count > 0 || session.Client.Store.View is not { Stage: "Planning", Private: { } own } view
            || view.Players.Single(player => player.PlayerId == own.PlayerId).Submitted) { return; }
        Run(async () =>
        {
            var ack = await session.SubmitAsync(new CancelPlanPayload(id));
            await session.Client.SynchronizeAsync();
            if (_battle && _session == session)
            { Render(session.Client.Store.View!); _status.Text = ack.Accepted ? "已撤回，卡牌返回手中。" : Ui.Reason(ack.Code); }
        });
    }
}
