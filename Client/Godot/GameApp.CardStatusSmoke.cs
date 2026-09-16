using Eota.Transport.Contracts;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private async Task CardStatusSmokeAsync()
    {
        try
        {
            _busy = true; Ui.Clear(_root);
            _root.AddChild(Ui.Text("卡牌状态 · 迟缓与种族", 26));
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 20); _root.AddChild(row);
            var titan = _catalog.Cards.Single(card => card.Id == "EOTA-CORE-ART-MIN-021");
            var instance = new CardView(1, titan.Id, "Minion", titan.Cost, titan.Attack, titan.Health, [new("Slow", 4)]);
            var prototype = CardTile.CreatePrototype(titan); Add("原型", prototype);
            var hand = CardTile.CreateInstance(titan, instance, false); Add("手牌实例", hand);
            var battle = CardTile.CreateInstance(titan, instance, true); Add("战场实例", battle);
            var firearm = CardTile.CreatePrototype(_catalog.Cards.Single(card => card.Id == "EOTA-CORE-GUA-MIN-021"), true); Add("火器营", firearm);
            var hound = CardTile.CreateThumbnail(_catalog.Cards.Single(card => card.Id == "EOTA-TOKEN-HUN-MIN-001")); Add("衍生卡", hound);
            Require(!prototype.GetNode<SlowMarkers>("SlowMarkers").Visible, "Prototypes have no current slow marker");
            Require(hand.GetNode<SlowMarkers>("SlowMarkers").Turns == 4, "Hand shows four slow icons");
            Require(firearm.GetNode<Label>("Type").Text == "火器营·随从" && hound.GetNode<Label>("Type").Text == "野兽·随从", "Tags are visible under cards");
            var entity = new EntityView(1, instance, 0, 0, 0, titan.Attack, titan.Health, titan.Health, null, false, false, 4, instance.Keywords);
            battle.UpdateCard(instance, entity);
            await CaptureInteraction("card-status-four");
            battle.UpdateCard(instance, entity with { SlowTurnsRemaining = 3 });
            Require(battle.GetNode<SlowMarkers>("SlowMarkers").Turns == 3, "Board count comes from entity, not prototype keyword");
            await CaptureInteraction("card-status-three");
            battle.UpdateCard(instance, entity with { SlowTurnsRemaining = 0 });
            Require(!battle.GetNode<SlowMarkers>("SlowMarkers").Visible, "No slow icons after waking");
            var soldier = CardTile.CreatePrototype(_catalog.Cards.Single(card => card.Id == "EOTA-CORE-GUA-MIN-001"));
            Require(soldier.GetNode<Label>("Type").Text == "随从", "Guardian does not infer a soldier tag"); soldier.Free();
            GD.Print("CARD_STATUS_SMOKE_OK slow=4-to-3-to-0 hand=instance prototype=hidden tags=visible");
            GetTree().Quit();
            void Add(string label, Control card)
            {
                var column = new VBoxContainer(); row.AddChild(column); column.AddChild(Ui.Text(label, 18)); column.AddChild(card);
            }
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
