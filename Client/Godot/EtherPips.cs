using Godot;

namespace Eota.GodotClient;

public partial class EtherPips : Control
{
    [Export] public Texture2D PipTexture { get; set; } = null!;
    public long Level { get; private set; }
    public int PlayerId { get; private set; }
    public int VisiblePipCount => (int)Math.Min(Math.Abs((decimal)Level), Capacity);
    private int Capacity => Math.Max(1, (int)Size.X / 28);

    public override void _Ready()
    {
        GetNode<HScrollBar>("More").ValueChanged += _ => QueueRedraw();
        Resized += Refresh; Refresh();
    }
    public void Bind(int playerId, long level)
    {
        PlayerId = playerId; Level = level;
        TooltipText = $"玩家 {playerId + 1} 的以太等级：{level}，仅对该玩家生效";
        Refresh();
    }
    private void Refresh()
    {
        Visible = Level != 0;
        var more = GetNode<HScrollBar>("More");
        var total = (double)Math.Abs((decimal)Level);
        more.MaxValue = Math.Max(Capacity, total); more.Page = Capacity; more.Visible = total > Capacity;
        QueueRedraw();
    }
    public override void _Draw()
    {
        if (Level == 0 || PipTexture is null) { return; }
        var start = (decimal)Math.Floor(GetNode<HScrollBar>("More").Value);
        var count = (int)Math.Clamp(Math.Abs((decimal)Level) - start, 0, Capacity);
        var left = (Size.X - count * 28 + 6) / 2;
        for (var index = 0; index < count; index++)
        {
            // Crop transparent recipe padding at draw time, keeping the source artwork intact.
            DrawTextureRectRegion(PipTexture, new Rect2(left + index * 28, 1, 22, 22), new Rect2(128, 112, 256, 256),
                Level < 0 ? new Color("e59b88") : Colors.White);
        }
    }
}
