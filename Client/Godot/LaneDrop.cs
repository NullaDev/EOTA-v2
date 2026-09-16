using Godot;

namespace Eota.GodotClient;

public partial class LaneDrop : PanelContainer
{
    public int? LaneId { get; set; }
    public Func<ulong, int?, bool>? Allowed { get; set; }
    public Action<ulong, int?>? Dropped { get; set; }
    public Action<int?>? Clicked { get; set; }
    public override bool _CanDropData(Vector2 atPosition, Variant data) => data.VariantType == Variant.Type.String
        && ulong.TryParse(data.AsString(), out var id) && Allowed?.Invoke(id, LaneId) == true;
    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (_CanDropData(atPosition, data)) { Dropped?.Invoke(ulong.Parse(data.AsString()), LaneId); }
    }
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { Clicked?.Invoke(LaneId); }
    }
}
