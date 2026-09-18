using Eota.Client.Desktop;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private DesktopProtocol _protocol = DesktopProtocol.Default;
    private Control _menuScreen = null!;
    private string _seedText = NewSeed();
    private int _aiDifficulty = 1;
    private int _setupTab = 7;
    private ILocalServerLauncher _serverLauncher = null!;

    private static string NewSeed() => GD.Randi().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void BuildMenu()
    {
        CaptureEditorDraft();
        for (var i = 0; i < _decks.Count; i++)
        { if (!_deckFiles.ContainsKey(_decks[i]) && _decks[i].Name == _decks[i].Profession) { _decks[i] = _catalog.DefaultDeck(_decks[i].Profession, _protocol); } }
        _battle = false; Ui.Clear(_root);
        _menuScreen = Ui.Instantiate<Control>("MenuScreen");
        _root.AddChild(_menuScreen);
        var tabs = _menuScreen.GetNode<TabContainer>("Tabs");
        var pages = new[] { "Local", "Remote", "Host", "Library", "DeckBuilder", "Protocol", "Replay", "Ai", "Editor" };
        var titles = new[] { "本机双人", "加入服务器", "本机开服", "卡牌图鉴", "牌组工坊", "对战协议", "对局回放", "人机对战", "卡牌编辑器" };
        for (var index = 0; index < pages.Length; index++)
        {
            var target = index; tabs.SetTabTitle(index, titles[index]);
            _menuScreen.GetNode<Button>("Navigation/" + pages[index]).Pressed += () => tabs.CurrentTab = target;
        }
        void RefreshNavigation()
        {
            for (var index = 0; index < pages.Length; index++)
            { _menuScreen.GetNode<Button>("Navigation/" + pages[index]).SetPressedNoSignal(index == tabs.CurrentTab); }
        }
        tabs.TabChanged += _ => RefreshNavigation(); tabs.CurrentTab = _setupTab; RefreshNavigation();
        _status = _menuScreen.GetNode<Label>("Status");
        var summaries = new List<Label>();
        foreach (var pageName in new[] { "Local", "Ai" })
        {
            var setup = tabs.GetNode<Control>(pageName); var againstAi = pageName == "Ai";
            var one = setup.GetNode<OptionButton>("One"); var two = setup.GetNode<OptionButton>("Two");
            foreach (var deck in _decks)
            {
                var title = $"{(deck.Name == deck.Profession ? "默认牌组" : deck.Name)} · {Ui.Profession(deck.Profession)} · {deck.Cards.Sum(card => card.Copies)} 张";
                one.AddItem(title); two.AddItem(title);
            }
            one.Select(0); two.Select(1);
            var seed = setup.GetNode<LineEdit>("Seed"); seed.Text = _seedText; seed.TextChanged += value => _seedText = value;
            // The field arrives with a fresh seed, so a reroll is only needed to change it on purpose.
            var reroll = setup.GetNode<Button>("RandomSeed");
            reroll.TooltipText = "重新生成一个种子";
            reroll.Pressed += () => { _seedText = NewSeed(); seed.Text = _seedText; };
            var summary = setup.GetNode<Label>("ProtocolSummary"); summary.Text = _protocol.Summary; summaries.Add(summary);
            if (againstAi)
            {
                var difficulty = setup.GetNode<OptionButton>("Difficulty");
                foreach (var option in DesktopCatalog.AiDifficulties) { difficulty.AddItem(option.Name); }
                difficulty.Select(_aiDifficulty);
                void RefreshAiHint() => setup.GetNode<Label>("AiNote").Text = DesktopCatalog.AiDifficulties[_aiDifficulty].Description;
                difficulty.ItemSelected += index => { _aiDifficulty = (int)index; RefreshAiHint(); }; RefreshAiHint();
            }
            setup.GetNode<Button>("Start").Pressed += () => Run(async () =>
            {
                if (!ulong.TryParse(seed.Text, out var value)) { throw new InvalidDataException("请输入非负整数种子。"); }
                _setupTab = setup.GetIndex();
                await CloseSession();
                _session = await DesktopSession.LocalAsync(_catalog, _decks[one.Selected], _decks[two.Selected],
                    new LocalMatchSettings(Seed: value, ProtocolJson: _protocol.Json,
                        Ai: againstAi ? new DesktopAiSettings(DesktopCatalog.AiDifficulties[_aiDifficulty].Id, value ^ 104729UL) : null));
                BuildBattle();
            });
            setup.GetNode<Button>("Protocol").Pressed += () => { _setupTab = setup.GetIndex(); tabs.CurrentTab = tabs.GetNode<Control>("Protocol").GetIndex(); };
        }
        BuildHostedServer(tabs, summaries);
        BuildCollection(tabs); BuildDeckBuilder(tabs); BuildReplayMenu(tabs);
        BuildContentEditor(tabs);
        BuildProtocolPage(tabs, summaries);
        _status.Text = "";
    }

    private int BuildProtocolPage(TabContainer tabs, IReadOnlyList<Label> summaries)
    {
        var page = tabs.GetNode<Control>("Protocol"); var index = page.GetIndex();
        var groups = page.GetNode<TabContainer>("Groups");
        var readValues = new Dictionary<string, Func<string>>(StringComparer.Ordinal);
        void Populate()
        {
            Ui.Clear(groups); readValues.Clear();
            foreach (var group in _protocol.Fields.Where(field => field.Group != "版本与结算限制" && field.Key != "drawAllocationPolicy").GroupBy(field => field.Group))
            {
                var scroll = Ui.Instantiate<ScrollContainer>("ProtocolGroup"); scroll.Name = group.Key;
                groups.AddChild(scroll);
                var rows = scroll.GetNode<VBoxContainer>("Rows");
                foreach (var field in group)
                {
                    var row = Ui.Instantiate<HBoxContainer>("ProtocolField");
                    rows.AddChild(row); row.GetNode<Label>("Label").Text = field.Label;
                    var number = row.GetNode<LineEdit>("Number"); number.Visible = field.Editable && field.Kind == "number";
                    if (!field.Editable)
                    {
                        var label = row.GetNode<Label>("Value"); label.Show();
                        label.Text = field.Choices.FirstOrDefault(choice => choice.Value == field.Value)?.Label ?? field.Value;
                    }
                    else if (field.Kind == "boolean")
                    {
                        var toggle = row.GetNode<CheckBox>("Toggle"); toggle.Show(); toggle.ButtonPressed = bool.Parse(field.Value);
                        readValues[field.Key] = () => toggle.ButtonPressed.ToString();
                    }
                    else if (field.Kind == "choice")
                    {
                        var choice = row.GetNode<OptionButton>("Choice"); choice.Show();
                        foreach (var option in field.Choices) { choice.AddItem(option.Label); }
                        choice.Select(field.Choices.IndexOf(field.Choices.First(option => option.Value == field.Value)));
                        readValues[field.Key] = () => field.Choices[choice.Selected].Value;
                    }
                    else { number.Text = field.Value; readValues[field.Key] = () => number.Text; }
                }
            }
        }
        page.GetNode<Button>("Actions/Apply").Pressed += () =>
        {
            try
            {
                var selected = _protocol.WithValues(readValues.ToDictionary(pair => pair.Key, pair => pair.Value(), StringComparer.Ordinal));
                File.WriteAllText(ProjectSettings.GlobalizePath("user://protocol.json"), selected.Json);
                _protocol = selected; BuildMenu(); _menuScreen.GetNode<TabContainer>("Tabs").CurrentTab = 5;
                _status.Text = "对战协议已保存，构筑与新对局将使用这些规则。";
            }
            catch (Exception error) { _status.Text = error.Message.Split('\n')[0]; }
        };
        page.GetNode<Button>("Actions/Reset").Pressed += () =>
        {
            _protocol = DesktopProtocol.Default;
            File.WriteAllText(ProjectSettings.GlobalizePath("user://protocol.json"), _protocol.Json);
            BuildMenu(); _menuScreen.GetNode<TabContainer>("Tabs").CurrentTab = 5;
            _status.Text = "已恢复默认对战协议。";
        };
        page.GetNode<Button>("Actions/Back").Pressed += () => tabs.CurrentTab = _setupTab;
        Populate(); return index;
    }
}
