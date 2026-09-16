using Godot;

namespace Eota.GodotClient;

// Visual time is advanced by the presentation player, never by the rules engine.
public partial class BattleAnimations : Control
{
    private sealed class Track(Control visual, double duration, double delay, Action<float> update, Action? restore)
    {
        public Control Visual { get; } = visual;
        public double Duration { get; } = duration;
        public double Time { get; set; } = -delay;
        public Action<float> Update { get; } = update;
        public Action? Restore { get; } = restore;
    }

    private readonly List<Track> _tracks = [];
    public bool HasActive => _tracks.Count > 0;

    public void Advance(double delta)
    {
        foreach (var track in _tracks.ToArray())
        {
            track.Time += delta;
            if (track.Time < 0) { continue; }
            track.Visual.Show();
            track.Update((float)Math.Clamp(track.Time / track.Duration, 0, 1));
            if (track.Time >= track.Duration) { Finish(track); }
        }
    }

    public void Clear()
    {
        foreach (var track in _tracks.ToArray()) { Finish(track); }
    }

    public override void _ExitTree() => Clear();

    private void Finish(Track track)
    {
        track.Restore?.Invoke();
        if (GodotObject.IsInstanceValid(track.Visual)) { RemoveChild(track.Visual); track.Visual.QueueFree(); }
        _tracks.Remove(track);
    }

    private void Add(Control visual, double duration, double delay, Action<float> update, Action? restore = null)
    {
        AddChild(visual); IgnoreInput(visual); visual.Hide();
        _tracks.Add(new Track(visual, duration, delay, update, restore));
    }

    private static void IgnoreInput(Node node)
    {
        if (node is Control control) { control.MouseFilter = MouseFilterEnum.Ignore; control.TooltipText = ""; }
        foreach (var child in node.GetChildren()) { IgnoreInput(child); }
    }

    private static bool Alive(Control node) => GodotObject.IsInstanceValid(node) && node.IsInsideTree() && !node.IsQueuedForDeletion();
    private static Action Conceal(Control? node)
    {
        if (node is null || !Alive(node)) { return () => { }; }
        var color = node.Modulate; node.Modulate = color with { A = 0 };
        return () => { if (Alive(node)) { node.Modulate = color; } };
    }

    public void DrawCard(Control ghost, Vector2 source, Func<Vector2> destination, Control? handCard, double delay)
    {
        ghost.Size = ghost.CustomMinimumSize; ghost.PivotOffset = ghost.Size / 2;
        var restore = Conceal(handCard);
        Add(ghost, 0.62, delay, progress =>
        {
            var eased = 1 - Mathf.Pow(1 - progress, 3);
            ghost.Scale = Vector2.One * Mathf.Lerp(0.48f, 1, eased);
            ghost.Rotation = Mathf.Sin(progress * Mathf.Pi) * -0.12f;
            var center = source.Lerp(destination(), eased) + new Vector2(0, -Mathf.Sin(progress * Mathf.Pi) * 100);
            ghost.GlobalPosition = center - ghost.Size / 2;
            ghost.Modulate = Colors.White with { A = Mathf.Min(progress * 10, 1) };
        }, restore);
    }

    public void Attack(CardTile ghost, CardTile attacker, float targetY, bool spark)
    {
        ghost.Size = attacker.Size; ghost.PivotOffset = ghost.Size / 2;
        var origin = attacker.GetGlobalRect().GetCenter();
        var target = new Vector2(origin.X, targetY);
        var contact = origin.Lerp(target, 0.46f);
        var restore = Conceal(attacker);
        Add(ghost, 0.56, 0, progress =>
        {
            var amount = progress < 0.36f ? Mathf.Pow(progress / 0.36f, 2)
                : progress < 0.52f ? 1 : 1 - Mathf.SmoothStep(0, 1, (progress - 0.52f) / 0.48f);
            ghost.GlobalPosition = origin.Lerp(contact, amount) - ghost.Size / 2;
            ghost.Scale = Vector2.One * (1 + amount * 0.09f);
        }, restore);
        if (!spark) { return; }
        var flash = Ui.Instantiate<Label>("ImpactFlash");
        flash.Size = flash.CustomMinimumSize; flash.PivotOffset = flash.Size / 2;
        Add(flash, 0.26, 0.19, progress =>
        {
            flash.GlobalPosition = origin.Lerp(target, 0.5f) - flash.Size / 2;
            flash.Scale = Vector2.One * (0.6f + progress * 1.1f);
            flash.Modulate = Colors.White with { A = 1 - progress };
        });
    }

    public void Float(string text, Vector2 position, Color color)
    {
        var label = Ui.Text(text, 24);
        Add(label, 0.48, 0, progress =>
        {
            label.GlobalPosition = position + new Vector2(0, -progress * 42);
            label.Modulate = color with { A = 1 - progress };
        });
    }
}
