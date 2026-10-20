using Eota.Client.Desktop;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private readonly List<Control> _battleTools = [];
    private static readonly string[] MenuToolNames = ["Replay", "Protocol", "Editor"];

    private static void AddTableBackdrop(Control screen, bool board)
    {
        var backdrop = new TableBackdrop { Name = "TableBackdrop", Board = board };
        screen.AddChild(backdrop); screen.MoveChild(backdrop, 0);
    }

    private void ConfigureMenuNavigation(TabContainer tabs)
    {
        AddTableBackdrop(_menuScreen, false);
        var play = _menuScreen.GetNode<Button>("Navigation/Play");
        var more = _menuScreen.GetNode<Button>("Navigation/More");
        play.Pressed += () => tabs.CurrentTab = _setupTab is 0 or 1 or 2 or 7 ? _setupTab : 7;
        more.Toggled += open =>
        {
            foreach (var name in MenuToolNames) { _menuScreen.GetNode<Control>("Navigation/" + name).Visible = open; }
        };
        void Refresh()
        {
            var isPlay = tabs.CurrentTab is 0 or 1 or 2 or 7;
            play.SetPressedNoSignal(isPlay);
            foreach (var name in new[] { "Ai", "Local", "Remote", "Host" })
            { _menuScreen.GetNode<Control>("Navigation/" + name).Visible = isPlay; }
            more.ButtonPressed = false;
            _status.Text = "";
        }
        tabs.TabChanged += _ => Refresh(); Refresh();
    }

    private void ConfigurePreparation(Control setup)
    {
        var advanced = setup.GetNode<Button>("Advanced");
        advanced.Toggled += open =>
        {
            advanced.Text = open ? "收起对战设置  ▴" : "对战设置  ▾";
            foreach (var name in new[] { "SeedTitle", "Seed", "RandomSeed", "ProtocolPanel", "ProtocolSummary", "Protocol" })
            { setup.GetNode<Control>(name).Visible = open; }
        };
        foreach (var prefix in new[] { "One", "Two" })
        {
            var choice = setup.GetNode<OptionButton>(prefix);
            void Refresh()
            {
                if (choice.Selected < 0 || choice.Selected >= _decks.Count) { return; }
                var deck = _decks[choice.Selected];
                setup.GetNode<Label>(prefix + "Mark").Text = ProfessionCrest(deck.Profession);
                setup.GetNode<Label>(prefix + "Hint").Text = Ui.Profession(deck.Profession);
                choice.TooltipText = deck.Name == deck.Profession ? Ui.Profession(deck.Profession) + "默认牌组" : deck.Name;
            }
            choice.ItemSelected += _ => Refresh(); Refresh();
        }
    }

    private void ConfigureBattleTools()
    {
        AddTableBackdrop(_battleScreen, true);
        _battleTools.Clear();
        foreach (var name in new[] { "Restart", "SaveReplay", "Resync", "SaveDiagnostics", "Pause", "Speed", "Skip" })
        {
            var control = _battleScreen.GetNode<Control>("Toolbar/" + name);
            if (control.Visible) { _battleTools.Add(control); }
            control.Hide();
        }
        var options = _battleScreen.GetNode<Button>("Toolbar/Options");
        options.Toggled += open =>
        {
            for (var index = 0; index < _battleTools.Count; index++)
            {
                _battleTools[index].Position = new Vector2(1388, 84 + index * 52);
                _battleTools[index].Visible = open;
            }
        };
        // Replay progress belongs beside the playback controls, outside the hover inspection.
        if (_replaySlider is { } slider)
        {
            slider.Reparent(_battleScreen, false);
            slider.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
            slider.Position = new Vector2(1020, 26); slider.Size = new Vector2(300, 32);
        }
        _battleScreen.GetNode<Control>("DetailPanel").Hide();
    }

    private void PositionInspection(Control owner)
    {
        var panel = _battleScreen.GetNode<Control>("DetailPanel");
        var bounds = owner.GetGlobalRect();
        var x = bounds.GetCenter().X < 800 ? bounds.End.X + 14 : bounds.Position.X - 306;
        panel.Position = new Vector2(Mathf.Clamp(x, 16, 1292), Mathf.Clamp(bounds.Position.Y - 160, 100, 416));
        panel.Show();
        // Inspection is informational; it must never steal the pointer from its source card.
        foreach (var control in Descendants(panel).OfType<Control>()) { control.MouseFilter = MouseFilterEnum.Ignore; }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) { return; }
        if (_battle && GodotObject.IsInstanceValid(_battleScreen))
        {
            _battleScreen.GetNode<Button>("Toolbar/Options").ButtonPressed = false;
            _selected = null; Highlight();
        }
        else if (GodotObject.IsInstanceValid(_menuScreen))
        { _menuScreen.GetNode<Button>("Navigation/More").ButtonPressed = false; }
        GetViewport().SetInputAsHandled();
    }
}
