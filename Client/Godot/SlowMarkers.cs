using Godot;

namespace Eota.GodotClient;

// The scrollable row shares the hand/battle/detail layouts. Only visible icons are drawn,
// so a Mod's large slow count cannot allocate one UI node per remaining turn.
public partial class SlowMarkers : ScrollContainer
{
    public long Turns { get; private set; }
    private SlowIconStrip? _strip;
    public void Bind(long turns)
    {
        Turns = Math.Max(0, turns); Visible = Turns > 0;
        if (_strip is null)
        {
            _strip = new SlowIconStrip { MouseFilter = MouseFilterEnum.Ignore };
            AddChild(_strip);
            GetHScrollBar().ValueChanged += _ => _strip.QueueRedraw();
            Resized += () => _strip.QueueRedraw();
        }
        _strip.Count = Turns;
        // UI coordinates have finite precision; the tooltip always retains the exact count.
        _strip.CustomMinimumSize = new Vector2((float)Math.Min(Turns, 100000) * 26, 26);
        _strip.QueueRedraw();
    }
}

public partial class SlowIconStrip : Control
{
    public long Count { get; set; }
    public override void _Draw()
    {
        var scroll = GetParent<ScrollContainer>();
        var first = Math.Max(0, scroll.ScrollHorizontal / 26);
        var end = Math.Min(Count, first + (int)Math.Ceiling(scroll.Size.X / 26) + 1);
        var texture = Ui.Texture("res://Content/Generated/Icons/slow-minion.png");
        DrawRect(new Rect2(first * 26, 0, (end - first) * 26, 26), new Color(0.05f, 0.08f, 0.06f, 0.72f));
        for (var index = first; index < end; index++)
        {
            // Icon exports use the same padded canvas as ether and frozen markers.
            DrawTextureRectRegion(texture, new Rect2(index * 26 + 1, 1, 24, 24), new Rect2(128, 112, 256, 256));
        }
    }
}
