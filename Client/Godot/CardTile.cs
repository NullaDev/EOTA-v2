using Eota.Client.Desktop;
using Eota.Transport.Contracts;
using Godot;

namespace Eota.GodotClient;

public partial class CardTile : Panel
{
    public ulong? CardId { get; private set; }
    public bool IsPrototype => CardId is null;
    public CardPresentation Prototype { get; private set; } = null!;
    public Func<bool>? CanDrag { get; set; }
    public Action<ulong>? Clicked { get; set; }
    public Action<CardPresentation>? PrototypeClicked { get; set; }
    public Action<CardPresentation>? Inspected { get; set; }
    public Action? InspectionEnded { get; set; }
    public bool IsHighlighted => GetNode<Panel>("Outline").Visible;
    public string StatsText { get; private set; } = "";

    public static CardTile CreatePrototype(CardPresentation prototype, bool detail = false) =>
        Create(prototype, detail ? "CardDetail" : "CardTile");
    public static CardTile CreateThumbnail(CardPresentation prototype) => Create(prototype, "CardMini");

    public static CardTile CreateInstance(CardPresentation prototype, CardView instance, bool compact)
    {
        var tile = Create(prototype, compact ? prototype.Kind == "Field" ? "FieldTile" : "MinionTile" : "CardTile");
        tile.MouseFilter = compact ? MouseFilterEnum.Pass : MouseFilterEnum.Stop;
        tile.UpdateCard(instance); return tile;
    }

    private static CardTile Create(CardPresentation prototype, string scene)
    {
        var tile = Ui.Instantiate<CardTile>(scene);
        tile.Prototype = prototype;
        tile.GetNode<Label>("Name").Text = prototype.Name;
        tile.GetNode<Label>("Description").Text = prototype.Description;
        tile.GetNode<TextureRect>("ArtFrame/Art").Texture = Ui.Texture("res://" + prototype.TexturePath);
        tile.GetNode<Label>("CostBox/Value").Text = prototype.Cost.ToString();
        tile.GetNode<Control>("CostBox").TooltipText = "费用";
        tile.GetNode<Control>("AttackBox").TooltipText = "攻击力";
        tile.GetNode<Control>("HealthBox").TooltipText = "最大生命";
        tile.GetNode<Control>("AttackBox").Visible = prototype.Kind == "Minion";
        tile.GetNode<Control>("HealthBox").Visible = prototype.Kind == "Minion" || prototype.Durability is not null;
        tile.GetNode<Label>("AttackBox/Value").Text = prototype.Attack?.ToString() ?? "";
        tile.GetNode<Label>("HealthBox/Value").Text = prototype.Health?.ToString() ?? "";
        if (prototype.Kind == "Field")
        {
            tile.GetNode<Panel>("HealthBox").ThemeTypeVariation = "Cost";
            tile.GetNode<Label>("HealthBox/Value").Text = prototype.Durability?.ToString() ?? "";
            tile.GetNode<Control>("HealthBox").TooltipText = "初始耐久 " + prototype.Durability;
        }
        tile.GetNode<Label>("Type").Text = prototype.Kind == "Minion" ? prototype.TypeLabel : prototype.Kind == "Field" ? "场地" : prototype.Global ? "全局法术" : "路线法术";
        tile.GetNode<Label>("Type").TooltipText = prototype.TypeLabel;
        if (prototype.Speed is { } speed)
        { tile.GetNode<Label>("Type").Text = (speed == "Fast" ? "快速" : "慢速") + (prototype.Global ? " · 全局法术" : " · 路线法术"); }
        tile.StatsText = prototype.Kind == "Minion" ? $"攻击 {prototype.Attack} · 最大生命 {prototype.Health}"
            : prototype.Kind == "Field" ? prototype.Durability is { } durability ? $"初始耐久 {durability}" : "永久场地" : "";
        tile.SetTooltip(); tile.MouseEntered += () => tile.Inspected?.Invoke(prototype);
        tile.MouseExited += () => tile.InspectionEnded?.Invoke();
        return tile;
    }

    public void Highlight(bool enabled)
    { GetNode<Panel>("Outline").Visible = enabled; }

    public void UpdateCard(CardView instance, EntityView? entity = null)
    {
        CardId = instance.CardInstanceId;
        GetNode<Label>("CostBox/Value").Text = instance.Cost.ToString();
        if (instance.CardKind == "Minion")
        {
            var attack = entity?.Attack ?? instance.Attack;
            var health = entity?.CurrentHealth ?? instance.MaximumHealth;
            var maximum = entity?.MaximumHealth ?? instance.MaximumHealth;
            GetNode<Label>("AttackBox/Value").Text = attack?.ToString() ?? "";
            GetNode<Label>("HealthBox/Value").Text = health?.ToString() ?? "";
            SetStatColor("AttackBox/Value", attack < Prototype.Attack ? "stat_reduced" : attack > Prototype.Attack ? "stat_increased" : null);
            SetStatColor("HealthBox/Value", health < maximum ? "stat_reduced"
                : health == maximum && health > Prototype.Health ? "stat_increased" : null);
            GetNode<Control>("HealthBox").TooltipText = entity is null ? $"最大生命 {maximum}" : $"生命 {health}/{maximum}";
            StatsText = entity is null ? $"攻击 {attack} · 最大生命 {maximum}" : $"攻击 {attack} · 生命 {health}/{maximum}";
        }
        else if (entity is not null)
        {
            GetNode<Label>("FieldInfo").Hide();
            GetNode<Control>("HealthBox").Visible = !entity.PermanentField;
            GetNode<Label>("HealthBox/Value").Text = entity.FieldEnergy?.ToString() ?? "";
            GetNode<Control>("HealthBox").TooltipText = "剩余耐久 " + entity.FieldEnergy;
            StatsText = entity.PermanentField ? "永久场地" : $"剩余耐久 {entity.FieldEnergy}";
        }
        SetTooltip();
        var slow = entity?.SlowTurnsRemaining ?? (instance.Keywords.IsDefault ? 0 : instance.Keywords.Where(keyword => keyword.Kind == "Slow").Select(keyword => keyword.Parameter).DefaultIfEmpty(0).Max());
        GetNodeOrNull<SlowMarkers>("SlowMarkers")?.Bind(slow);
        if (slow > 0) { TooltipText += $"\n迟缓 {slow}"; }
    }

    private void SetTooltip() => TooltipText = Prototype.Name + "\n" + Prototype.Description + "\n" + StatsText;

    private void SetStatColor(string path, string? color)
    {
        var label = GetNode<Label>(path);
        if (color is null) { label.RemoveThemeColorOverride("font_color"); }
        else { label.AddThemeColorOverride("font_color", label.GetThemeColor(color)); }
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { return; }
        if (CardId is { } id && Clicked is not null) { Clicked(id); AcceptEvent(); }
        else if (CardId is null && PrototypeClicked is not null) { PrototypeClicked(Prototype); AcceptEvent(); }
    }
    public override Variant _GetDragData(Vector2 atPosition)
    {
        if (CardId is not { } id || CanDrag?.Invoke() != true) { return default; }
        SetDragPreview(Ui.Text(Prototype.Name, 24)); return id.ToString();
    }
}
