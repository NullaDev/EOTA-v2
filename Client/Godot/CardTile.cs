using Eota.Client.Desktop;
using Eota.Transport.Contracts;
using Godot;

namespace Eota.GodotClient;

public partial class CardTile : Panel
{
    // One explanation per keyword that appears on the card. Nothing else is repeated on hover.
    private static readonly Dictionary<string, string> KeywordBriefs = new(StringComparer.Ordinal)
    {
        ["迅捷"] = "入场当回合即可主动攻击",
        ["守备"] = "自己不主动攻击，被攻击时仍能防守",
        ["迟缓"] = "不能主动攻击或防守，每次战斗后减1",
        ["先攻"] = "先造成战斗伤害；击杀目标时对方无法反击",
        ["斩杀"] = "参与战斗伤害时直接消灭对撞的随从",
        ["吸血"] = "按对敌方英雄造成的战斗伤害回复己方英雄",
        ["游击"] = "本路有敌随从时，尝试移动到相邻空路",
        ["追猎"] = "尝试移动到相邻的敌方随从所在路",
        ["替换"] = "入场时可以替换本方同位置的已有对象",
        ["可替换"] = "允许被本方同类新对象替换",
        ["蓄能"] = "多张充能需求之和不超过剩余费用加蓄能时，这批充能一起发动",
        ["快速"] = "在入场效果之后结算，本回合随从入场时无法先享受它",
        ["慢速"] = "在战斗与移动之后结算"
    };
    private static DesktopCatalog? _catalog;
    private string _keywords = "";

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
    // The card face already shows its name, numbers and rules text, so hover carries keywords only.
    public string KeywordTooltip { get; private set; } = "";
    // Regression guard: a keyword may never be explained twice on the same card.
    public bool HasDuplicateKeywordLines() =>
        KeywordTooltip.Split('\n').Where(line => line.Length > 0).Distinct(StringComparer.Ordinal).Count()
        != KeywordTooltip.Split('\n').Count(line => line.Length > 0);

    public static CardTile CreatePrototype(CardPresentation prototype, bool detail = false) =>
        Create(prototype, detail ? "CardDetail" : "CardTile");
    public static CardTile CreateThumbnail(CardPresentation prototype) => Create(prototype, "CardMini");
    public static void UseCatalog(DesktopCatalog catalog) => _catalog = catalog;

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
        var printed = PrintedKeywords(prototype);
        tile._keywords = KeywordLines(prototype, printed, [], includePrintedBlock: true, related: TokenKeywords(prototype));
        tile.SetTooltip(tile._keywords); tile.MouseEntered += () => tile.Inspected?.Invoke(prototype);        tile.MouseExited += () => tile.InspectionEnded?.Invoke();
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
        var slow = entity?.SlowTurnsRemaining;
        GetNodeOrNull<SlowMarkers>("SlowMarkers")?.Bind(slow ?? 0);
        var live = LiveKeywords(instance, slow);
        SetTooltip(KeywordLines(Prototype, PrintedKeywords(Prototype), live, includePrintedBlock: true, related: TokenKeywords(Prototype)));
    }

    private static List<string> PrintedKeywords(CardPresentation prototype)
    {
        var printed = prototype.Keywords.ToList();
        if (prototype.Speed is { } speed) { printed.Add(speed == "Fast" ? "快速" : "慢速"); }
        return printed;
    }

    // Hover explains keywords only; the card face already shows its name, numbers and rules text.
    private void SetTooltip(string keywords)
    {
        KeywordTooltip = keywords;
        TooltipText = keywords;
    }

    // Only the keyword explanations, for a panel that already shows the card face and description.
    // Built from the same inputs as the hover tooltip, including keywords of referenced tokens.
    public static string KeywordLinesOnly(CardPresentation prototype, CardView? instance)
        => KeywordLines(prototype, PrintedKeywords(prototype), LiveKeywords(instance), includePrintedBlock: true, related: TokenKeywords(prototype));

    // The instance's live keywords, folding an entity's remaining slow into a single entry.
    public static List<KeywordView> LiveKeywords(CardView? instance, long? slowOverride = null)
    {
        var keywords = instance?.Keywords ?? [];
        var live = keywords.IsDefaultOrEmpty ? [] : keywords.Where(keyword => keyword.Kind != "Slow").ToList();
        var slow = slowOverride ?? (keywords.IsDefaultOrEmpty ? 0
            : keywords.Where(keyword => keyword.Kind == "Slow").Select(keyword => keyword.Parameter).DefaultIfEmpty(0).Max());
        if (slow > 0) { live.Add(new KeywordView("Slow", slow)); }
        return live;
    }

    // One explanation per keyword. A keyword the description spells out (for example "守备，可替换。")
    // is explained here too, so every keyword appears exactly once and no bare repeat of the card text
    // remains.
    private static string KeywordLines(CardPresentation prototype, IEnumerable<string> printed, IEnumerable<KeywordView>? instance,
        bool includePrintedBlock, IEnumerable<string>? related = null)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var original = prototype.Description;
        var description = original;
        if (includePrintedBlock && TryTakePrintedKeywords(ref description, out var block))
        {
            foreach (var name in block) { Add(name); }
        }
        foreach (var keyword in printed) { Add(keyword); }
        // Keywords the card only refers to (for example 举起盾牌！ asking whether the target has 守备).
        foreach (var keyword in MentionedKeywords(prototype, original, related)) { Add(keyword); }
        foreach (var keyword in instance ?? [])
        {
            Add(keyword.Kind == "Slow" ? "迟缓 " + keyword.Parameter : InstanceKeywordName(keyword.Kind));
        }
        foreach (var keyword in related ?? []) { Add(keyword); }
        return string.Join('\n', lines);
        void Add(string keyword)
        {
            if (keyword.Length == 0) { return; }
            var name = keyword.Split(' ')[0];
            var chinese = InstanceKeywordName(name);
            if (!seen.Add(name)) { return; }
            // "迟缓 3" keeps its count, plain names map straight through.
            var display = keyword == name ? chinese : chinese + keyword[name.Length..];
            lines.Add(display + (KeywordBriefs.TryGetValue(chinese, out var brief) ? "：" + brief : ""));
        }
    }

    // Keywords the card's own text refers to, or that its tokens print. A card like 举起盾牌！
    // never has 守备 itself, but the player still needs to know what the condition means.
    private static IEnumerable<string> MentionedKeywords(CardPresentation prototype, string description, IEnumerable<string>? related)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var keyword in MentionedIn(description)) { if (seen.Add(keyword)) { yield return keyword; } }
        foreach (var token in _catalog?.RelatedCards(prototype.Id) ?? [])
        {
            foreach (var keyword in MentionedIn(token.Description)) { if (seen.Add(keyword)) { yield return keyword; } }
        }
        foreach (var keyword in related ?? []) { if (seen.Add(keyword)) { yield return keyword; } }
    }

    // Matches the keyword spellings the card text uses. Longer names win and are masked out, so
    // "可替换" never also reports "替换" through substring overlap.
    private static IEnumerable<string> MentionedIn(string text)
    {
        var remaining = text;
        var names = new[] { "Swift", "Guard", "Slow", "Replace", "Replaceable", "Lifesteal", "Skirmisher", "Pursuit", "FirstStrike", "Execute" }
            .Select(InstanceKeywordName).Concat(["蓄能", "快速", "慢速"])
            .OrderByDescending(name => name.Length);
        foreach (var name in names)
        {
            if (!remaining.Contains(name, StringComparison.Ordinal)) { continue; }
            remaining = remaining.Replace(name, "\u0001", StringComparison.Ordinal);
            yield return name;
        }
    }

    // Keywords the card's summoned or generated tokens carry, so a spell like 紧急动员 also explains
    // the "可替换" printed on the 民兵 it creates. The Desktop catalog already resolves which prototypes
    // a card references, so the client stays free of Kernel types.
    private static IEnumerable<string> TokenKeywords(CardPresentation prototype)
    {
        foreach (var related in _catalog?.RelatedCards(prototype.Id) ?? [])
        {
            foreach (var keyword in PrintedKeywords(related)) { yield return keyword; }
        }
    }

    // True when the description opens with nothing but keyword names ("守备，可替换。"), in which case
    // those names are returned so the caller can explain them like any other keyword.
    private static bool TryTakePrintedKeywords(ref string description, out string[] names)
    {
        names = [];
        var end = description.IndexOfAny(['。', '.']);
        if (end < 0) { return false; }
        var segment = description[..(end + 1)];
        names = segment.Split(['，', ',', '、', ' ', '　'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.TrimEnd('。', '.')).Where(value => value.Length > 0).ToArray();
        if (names.Length == 0 || names.Any(name => !KeywordBriefs.ContainsKey(name))) { names = []; return false; }
        description = description[(end + 1)..].TrimStart();
        return true;
    }

    private static string InstanceKeywordName(string kind) => kind switch
    {
        "Swift" => "迅捷", "Guard" => "守备", "Slow" => "迟缓", "Replace" => "替换", "Replaceable" => "可替换",
        "Lifesteal" => "吸血", "Skirmisher" => "游击", "Pursuit" => "追猎", "FirstStrike" => "先攻", "Execute" => "斩杀",
        _ => kind
    };

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
