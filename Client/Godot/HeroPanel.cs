using Eota.Transport.Contracts;
using Godot;

namespace Eota.GodotClient;

public partial class HeroPanel : Panel
{
    public void Bind(PublicPlayerView player)
    {
        GetNode<Label>("Profession").Text = Ui.Profession(player.Profession);
        GetNode<Label>("Crest").Text = player.Profession switch { "Guardian" => "♜", "Arcanist" => "✦", "Artisan" => "⚒", "Hunter" => "➶", "Soulweaver" => "☽", _ => "◇" };
        GetNode<Label>("Seat").Text = $"玩家 {player.PlayerId + 1}";
        GetNode<Label>("Health/Value").Text = player.HeroHealth.ToString();
        GetNode<Control>("Health").TooltipText = $"生命 {player.HeroHealth}/{player.HeroMaximumHealth}";
        TooltipText = $"生命 {player.HeroHealth}/{player.HeroMaximumHealth} · 费用上限 {player.MaxCost}";
        if (player.FatigueCount > 0) { TooltipText += $"\n已疲劳 {player.FatigueCount} 次"; }
        GetNode<Label>("Resources").Text = $"手牌 {player.HandCount}\n牌库 {player.DeckCount}";
        GetNode<Label>("State").Text = player.Submitted ? "已提交 · 等待结算" : "正在规划";
        var poisoned = false;
        foreach (var effect in player.ActiveEffects)
        {
            var poison = effect.EffectId == "bloodPoison";
            poisoned |= poison;
            var name = poison ? "中毒" : effect.Trigger == "TurnEnd" ? "回合末效果" : "持续效果";
            TooltipText += $"\n{name} · " + (effect.RemainingDuration is { } turns ? $"剩余 {turns} 回合" : "持续生效");
        }
        if (poisoned) { GetNode<Label>("State").Text = (player.Submitted ? "已提交" : "规划中") + " · 中毒"; }
    }
}
