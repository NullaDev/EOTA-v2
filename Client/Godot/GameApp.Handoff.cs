using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private void ShowHandoff()
    {
        if (_busy || _session is not { CanSwitchSeat: true } session) { return; }
        // Remove the entire battle before yielding or switching the observer. No hand or queued
        // animation can remain behind the handoff screen, and the other seat is not selected yet.
        _battle = false; _frames.Clear(); _displayed = null; _selected = null; _mulligan.Clear();
        Ui.Clear(_root); _registry.Clear(); _plannedTiles.Clear();
        var screen = Ui.Instantiate<Control>("SeatHandoff");
        _root.AddChild(screen); _status = screen.GetNode<Label>("Status");
        var player = session.Client.Store.View!.Private!.PlayerId == 0 ? "玩家二" : "玩家一";
        screen.GetNode<Label>("Title").Text = "请交给" + player;
        var enter = screen.GetNode<Button>("Continue"); enter.Text = "我是" + player + "，继续对局";
        enter.Pressed += () => Run(async () =>
        {
            if (_session != session) { return; }
            enter.Disabled = true;
            session.SwitchSeat(); await session.Client.SynchronizeAsync(); BuildBattle();
        });
        screen.GetNode<Button>("Back").Pressed += () => Run(async () => { await CloseSession(); BuildMenu(); });
    }
}
