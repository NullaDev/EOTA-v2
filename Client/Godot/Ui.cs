using Godot;

namespace Eota.GodotClient;

internal static class Ui
{
    private static readonly Dictionary<string, PackedScene> Scenes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Texture2D> Textures = new(StringComparer.Ordinal);
    public static T Instantiate<T>(string sceneName) where T : Node
    {
        // Keep a strong managed reference while native instantiation builds the
        // node tree, and reuse the scene across frequent card/menu rebuilds.
        if (!Scenes.TryGetValue(sceneName, out var scene))
        {
            scene = ResourceLoader.Load<PackedScene>($"res://Client/Godot/Scenes/{sceneName}.tscn")
                ?? throw new IOException("Unable to load scene: " + sceneName);
            Scenes.Add(sceneName, scene);
        }
        return scene.Instantiate<T>();
    }
    public static Texture2D Texture(string path)
    {
        if (Textures.TryGetValue(path, out var cached)) { return cached; }
        Texture2D texture;
        var absolutePath = ProjectSettings.GlobalizePath(path);
        // Content generation rewrites these PNGs outside Godot's importer. During
        // development, prefer the current source file over a stale imported .ctex.
        // Exported builds fall back to ResourceLoader when no loose file exists.
        if (path.StartsWith("res://Content/Generated/", StringComparison.Ordinal) && File.Exists(absolutePath))
        {
            using var image = Image.LoadFromFile(absolutePath);
            if (image.IsEmpty()) { throw new IOException("Unable to load generated texture: " + path); }
            texture = ImageTexture.CreateFromImage(image);
        }
        else if (ResourceLoader.Exists(path)) { texture = ResourceLoader.Load<Texture2D>(path); }
        else
        {
            using var image = Image.LoadFromFile(absolutePath);
            if (image.IsEmpty()) { throw new IOException("Unable to load texture: " + path); }
            texture = ImageTexture.CreateFromImage(image);
        }
        Textures.Add(path, texture); return texture;
    }
    public static void ReleaseResources()
    {
        foreach (var scene in Scenes.Values) { scene.Dispose(); }
        Scenes.Clear();
        foreach (var texture in Textures.Values) { texture.Dispose(); }
        Textures.Clear();
    }
    public static readonly Color Background = new("171c19");
    public static readonly Color Panel = new("252d27");
    public static readonly Color Accent = new("d9b76e");
    public static StyleBoxFlat Box(Color color, int radius = 10, Color? border = null) => new()
    {
        BgColor = color,
        CornerRadiusTopLeft = radius,
        CornerRadiusTopRight = radius,
        CornerRadiusBottomLeft = radius,
        CornerRadiusBottomRight = radius,
        BorderWidthLeft = 1,
        BorderWidthTop = 1,
        BorderWidthRight = 1,
        BorderWidthBottom = 1,
        BorderColor = border ?? new Color("344655"),
        ContentMarginLeft = 10,
        ContentMarginRight = 10,
        ContentMarginTop = 8,
        ContentMarginBottom = 8
    };
    public static Label Text(string text, int size = 16) { var label = new Label { Text = text }; label.AddThemeFontSizeOverride("font_size", size); return label; }
    public static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(0, 38) };
        button.Pressed += action; return button;
    }
    public static void Clear(Node node)
    {
        foreach (var child in node.GetChildren()) { node.RemoveChild(child); child.QueueFree(); }
    }
    public static TextureRect Icon(string id, int size = 26) => new()
    {
        Texture = Texture($"res://Content/Generated/Icons/{id}.png"),
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        CustomMinimumSize = new Vector2(size, size),
        MouseFilter = Control.MouseFilterEnum.Ignore
    };
    public static string Profession(string value) => value switch { "Guardian" => "守护者", "Arcanist" => "奥术师", "Artisan" => "工匠", "Hunter" => "猎人", _ => "中立" };
    public static string Reason(string code) => code switch
    {
        "None" => "可以放置",
        "LaneLocked" => "这条路已锁闭",
        "InsufficientCost" => "费用不足",
        "BattlefieldSlotUnavailable" => "槽位已有卡牌，无法替换",
        "PlanningSlotOccupied" => "此槽位已有规划",
        "PlayerAlreadySubmitted" => "已提交，等待对方",
        "CardNotInHand" => "该卡牌已不在手牌中",
        "WrongStage" => "当前阶段无法规划",
        "MatchNotActive" => "对局已结束",
        "rule-content-mismatch" => "卡牌规则版本与服务器不同，请双方使用同一份卡牌内容后再连接。",
        _ => code
    };
}
