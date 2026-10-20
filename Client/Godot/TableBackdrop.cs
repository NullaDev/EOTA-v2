using Godot;

namespace Eota.GodotClient;

/// <summary>Quiet, resolution-independent tabletop decoration behind the playable controls.</summary>
public partial class TableBackdrop : Control
{
    public bool Board { get; init; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), Ui.Background);
        var line = new Color("444d3e", 0.35f);
        var light = new Color("b5a47e", 0.12f);
        // Broad translucent bands give the table depth without a noisy texture.
        for (var index = 0; index < 22; index++)
        {
            var inset = index * 28;
            DrawRect(new Rect2(inset, 92 + index * 14, Mathf.Max(0, 1600 - inset * 2), 780 - index * 22),
                new Color("63765c", 0.012f));
        }
        if (Board)
        {
            DrawRect(new Rect2(238, 80, 1124, 548), new Color("1c251f"));
            DrawRect(new Rect2(238, 80, 1124, 548), new Color("68735b", 0.45f), false, 1);
            DrawLine(new Vector2(248, 358), new Vector2(1352, 358), light, 1);
            DrawRect(new Rect2(172, 718, 1188, 282), new Color("141c17", 0.7f));
            DrawLine(new Vector2(186, 718), new Vector2(1348, 718), line);
        }
        else
        {
            DrawLine(new Vector2(40, 86), new Vector2(1560, 86), line);
            var center = new Vector2(800, 476);
            foreach (var radius in new[] { 296f, 308f, 360f })
            { DrawArc(center, radius, 0, Mathf.Tau, 128, new Color("a79b73", 0.055f), 1, true); }
            for (var index = 0; index < 12; index++)
            {
                var angle = index * Mathf.Tau / 12;
                var direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                DrawLine(center + direction * 296, center + direction * 308, light);
            }
        }
        // A restrained border and small brass inlays, like the edge of a game table.
        foreach (var x in new[] { 20f, 1580f })
        {
            DrawLine(new Vector2(x, 110), new Vector2(x, 930), line);
            DrawCircle(new Vector2(x, 96), 3, light);
            DrawCircle(new Vector2(x, 944), 3, light);
        }
    }
}
