using Eota.Transport.Contracts;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private BattleAnimations _animations = null!;
    private bool Animating => GodotObject.IsInstanceValid(_animations) && _animations.HasActive;

    private void AnimateFrame(PresentationFramePayload frame)
    {
        var own = frame.View.Private?.PlayerId ?? 0;
        var drawn = 0;
        var collisions = new HashSet<(ulong, ulong)>();
        foreach (var value in frame.Events)
        {
            if (value.Kind is "CardDrawn" or "CardGenerated" or "CardReturned" && value.PlayerId is { } player)
            {
                // The observer projection alone decides whether a card face is available.
                var handCard = value.Card is { } card ? _hand.GetChildren().OfType<CardTile>().FirstOrDefault(tile => tile.CardId == card.CardInstanceId) : null;
                Control ghost = value.Card is { } visible ? CardTile.CreateInstance(Presentation(visible), visible, false)
                    : Ui.Instantiate<Control>("CardBack");
                var hero = _heroes[player == own ? 0 : 1].GetGlobalRect();
                var source = hero.GetCenter();
                var handRect = _battleScreen.GetNode<ScrollContainer>("HandScroll").GetGlobalRect();
                Vector2 Destination()
                {
                    if (player != own || frame.View.Private is null) { return new Vector2(_battleScreen.GlobalPosition.X + 800, _battleScreen.GlobalPosition.Y + 94); }
                    var center = handCard is not null && GodotObject.IsInstanceValid(handCard) && handCard.IsInsideTree()
                        ? handCard.GetGlobalRect().GetCenter() : handRect.GetCenter();
                    return new Vector2(Math.Clamp(center.X, handRect.Position.X + 90, handRect.End.X - 90), handRect.GetCenter().Y);
                }
                _animations.DrawCard(ghost, source, Destination, handCard, drawn++ * 0.09);
            }
            if (value.Kind != "AttackDeclared" || value.EntityId is not { } id || !_registry.TryGetValue(id, out var attacker)) { continue; }
            var entity = frame.View.Entities.Single(entity => entity.EntityId == id);
            float targetY;
            var spark = true;
            if (value.TargetEntityId is { } targetId && _registry.TryGetValue(targetId, out var defender))
            {
                targetY = defender.GetGlobalRect().GetCenter().Y;
                spark = collisions.Add((Math.Min(id, targetId), Math.Max(id, targetId)));
            }
            // Hero attacks use the opposing slot on this lane, keeping both seats' motion vertical.
            else if (value.TargetPlayerId is { } targetPlayer)
            { targetY = _slots[(entity.LaneId, targetPlayer, "Minion")].GetGlobalRect().GetCenter().Y; }
            else { continue; }
            var clone = CardTile.CreateInstance(Presentation(entity.Card), entity.Card, true); clone.UpdateCard(entity.Card, entity);
            _animations.Attack(clone, attacker, targetY, spark);
        }
    }
}
