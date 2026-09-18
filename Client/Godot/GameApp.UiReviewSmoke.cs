using System.Collections.Immutable;
using Eota.Client.Desktop;
using Eota.Transport.Contracts;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private async Task UiReviewSmokeAsync()
    {
        try
        {
            var tabs = _menuScreen.GetNode<TabContainer>("Tabs");
            await CaptureUi("menu");
            _menuScreen.GetNode<Button>("Tabs/Local/Protocol").EmitSignal(BaseButton.SignalName.Pressed);
            if (tabs.GetTabTitle(tabs.CurrentTab) != "对战协议") { throw new InvalidOperationException("Protocol entry failed."); }
            await CaptureUi("protocol");
            tabs.CurrentTab = 3;
            var prototypes = Descendants(tabs.GetNode<Control>("Library")).OfType<CardTile>().ToArray();
            if (prototypes.Length != _catalog.Cards.Count(card => card.Source == "Core") || prototypes.Any(tile => !tile.IsPrototype || tile.StatsText.Contains('/')))
            { throw new InvalidOperationException("Collection cards must bind only prototypes and maximum health."); }
            await CaptureUi("collection");
            var showcase = prototypes.First(tile => tile.Prototype.Kind == "Minion");
            showcase.PrototypeClicked!(showcase.Prototype); await CaptureUi("card-detail");
            tabs.CurrentTab = 4;
            var builder = tabs.GetNode<Control>("DeckBuilder");
            var manager = builder.GetNode<Control>("Manager"); var editor = builder.GetNode<Control>("Editor");
            var picker = builder.GetNode<Control>("ProfessionPicker");
            await CaptureInteraction("deck-library");
            foreach (var index in Enumerable.Range(0, Professions.Length))
            {
                manager.GetNode<Button>("New").EmitSignal(BaseButton.SignalName.Pressed);
                picker.GetNode<Button>("Panel/Professions/" + Professions[index]).EmitSignal(BaseButton.SignalName.Pressed);
                Require(picker.Visible && picker.GetNode<Label>("Panel/Description").Text == ProfessionDescription(Professions[index]), "Profession description");
                await CaptureInteraction("profession-" + Professions[index]);
                if (index == 0) { await CaptureInteraction("profession-picker"); }
                picker.GetNode<Button>("Panel/Create").EmitSignal(BaseButton.SignalName.Pressed);
                Require(editor.Visible && !manager.Visible && !picker.Visible, "New deck opens editor");
                var pool = editor.GetNode<GridContainer>("Scroll/Grid").GetChildren().OfType<CardTile>().ToArray();
                if (pool.Length == 0 || pool.Any(tile => tile.Prototype.Source != "Core"
                    || tile.Prototype.Profession != "Neutral" && tile.Prototype.Profession != Professions[index]))
                { throw new InvalidOperationException("Deck pool must automatically filter by profession."); }
                pool.First().PrototypeClicked!(pool.First().Prototype);
                editor.GetNode<Button>("Back").EmitSignal(BaseButton.SignalName.Pressed);
            }
            manager.GetNode<GridContainer>("Scroll/Grid").GetChild<Button>(_decks.Count).EmitSignal(BaseButton.SignalName.Pressed);
            if (!editor.GetNode<Label>("Count").Text.StartsWith("1 / 40", StringComparison.Ordinal)) { throw new InvalidOperationException("Returning to the deck list must preserve drafts."); }
            editor.GetNode<Button>("Default").EmitSignal(BaseButton.SignalName.Pressed);
            await CaptureUi("deck-builder");
            editor.GetNode<Button>("Back").EmitSignal(BaseButton.SignalName.Pressed);
            manager.GetNode<GridContainer>("Scroll/Grid").GetChild<Button>(1).EmitSignal(BaseButton.SignalName.Pressed);
            Require(editor.GetNode<Label>("Count").Text.StartsWith("40 / 40", StringComparison.Ordinal), "Existing deck opens populated editor");
            var testDeckName = "UI验收-" + Guid.NewGuid().ToString("N");
            string? testDeckId = null;
            try
            {
                editor.GetNode<LineEdit>("DeckName").Text = testDeckName;
                editor.GetNode<LineEdit>("DeckName").EmitSignal(LineEdit.SignalName.TextChanged, testDeckName);
                editor.GetNode<Button>("Save").EmitSignal(BaseButton.SignalName.Pressed);
                testDeckId = _deckFiles.FirstOrDefault(pair => pair.Key.Name == testDeckName).Value;
                Require(testDeckId is not null && _deckStore.Read(testDeckId).Name == testDeckName, "Editor saves a valid named deck");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                Require(_menuScreen.GetNode<Control>("Tabs/DeckBuilder/Manager").IsVisibleInTree()
                    && _decks.Any(deck => deck.Name == testDeckName), "Save returns to manager and updates match choices");
            }
            finally
            {
                if (testDeckId is not null) { _deckStore.Delete(testDeckId); }
                foreach (var deck in _deckFiles.Keys.Where(deck => deck.Name == testDeckName).ToArray()) { _deckFiles.Remove(deck); }
                _decks.RemoveAll(deck => deck.Name == testDeckName);
            }
            BuildMenu(); tabs = _menuScreen.GetNode<TabContainer>("Tabs");
            _menuScreen.GetNode<Button>("Navigation/Host").EmitSignal(BaseButton.SignalName.Pressed);
            if (tabs.CurrentTab != 2 || tabs.GetNode<Button>("Host/Start").Disabled == _serverLauncher.IsAvailable
                || !tabs.GetNode<Label>("Host/Availability").Text.Contains(_serverLauncher.IsAvailable ? "已停止" : "缺少"))
            { throw new InvalidOperationException("Local hosting entry must state its implementation status."); }
            await CaptureUi("host");
            _session = await DesktopSession.LocalAsync(_catalog, _decks[0], _decks[1], new LocalMatchSettings(Mulligan: true));
            BuildBattle();
            await CaptureUi("mulligan");
            if (_etherPips.Values.Any(pips => pips.Visible)) { throw new InvalidOperationException("Zero ether must be hidden."); }
            if (_submit.Disabled || !_submit.Text.StartsWith("确认换牌", StringComparison.Ordinal))
            { throw new InvalidOperationException("Mulligan action must be available."); }
            CheckLayout();
            _busy = true;
            var first = _hand.GetChildren().OfType<CardTile>().First();
            first.Clicked!(first.CardId!.Value);
            await SubmitForSmoke(new SubmitMulliganPayload(_mulligan.ToImmutableArray()));
            _session.SwitchSeat(); await _session.Client.SynchronizeAsync();
            await SubmitForSmoke(new SubmitMulliganPayload([]));
            _session.SwitchSeat(); await _session.Client.SynchronizeAsync(); BuildBattle();
            for (var turn = 0; turn < 4; turn++)
            {
                for (var seat = 0; seat < 2; seat++)
                {
                    var own = _session.Client.Store.View!.Private!;
                    var option = own.PlanOptions.FirstOrDefault(value => value.Allowed);
                    if (option is not null)
                    {
                        var card = own.Hand.Single(value => value.CardInstanceId == option.CardInstanceId);
                        var payload = card.CardKind == "Spell" ? (ClientPayload)new PlanSpellPayload(card.CardInstanceId, option.LaneId)
                            : new PlanCardPayload(card.CardInstanceId, option.LaneId!.Value);
                        await SubmitForSmoke(payload);
                    }
                    await SubmitForSmoke(new SubmitTurnPayload());
                    _session.SwitchSeat(); await _session.Client.SynchronizeAsync();
                }
            }
            if (_session.Client.Store.View!.Turn != 5) { throw new InvalidOperationException("Four completed turns must reach turn 5."); }
            _busy = false;
            BuildBattle(); Inspect(_catalog.Cards.First(card => card.Kind == "Minion"));
            await CaptureUi("battle"); CheckLayout();
            await CheckEtherPresentation();
            await CloseSession();
            // More lanes scroll horizontally while the fixed slot dimensions remain unchanged.
            var wideProtocol = DesktopProtocol.Default.WithValues(new Dictionary<string, string>
            {
                ["laneCount"] = "12",
                ["openingHandSize"] = "10",
                ["handLimit"] = "10"
            });
            _session = await DesktopSession.LocalAsync(_catalog, _decks[0], _decks[1], new LocalMatchSettings(ProtocolJson: wideProtocol.Json));
            BuildBattle(); await CaptureUi("twelve-lanes"); CheckLayout();
            var board = _battleScreen.GetNode<ScrollContainer>("BoardScroll");
            board.ScrollHorizontal = (int)board.GetHScrollBar().MaxValue;
            var hand = _battleScreen.GetNode<ScrollContainer>("HandScroll");
            hand.ScrollHorizontal = (int)hand.GetHScrollBar().MaxValue;
            await CaptureUi("scroll-end"); CheckLayout();
            var lastLane = board.GetNode<HBoxContainer>("Lanes").GetChild<Control>(11);
            if (lastLane.GetGlobalRect().End.X > board.GetGlobalRect().End.X + 1
                || _hand.GetChildCount() != 10 || _hand.GetChild<Control>(9).GetGlobalRect().End.X > hand.GetGlobalRect().End.X + 1)
            { throw new InvalidOperationException("Last lane or hand card is unreachable by scrolling."); }
            GD.Print($"P9_UI_REVIEW_SMOKE_OK prototypes={prototypes.Length} decks={Professions.Length} manager=verified professions=described drafts=retained ether=per-player hosting=available layouts=6,12 hand=10 scroll=end turn=5");
            await CloseSession(); GetTree().Quit();
        }
        catch (Exception error) { _busy = false; GD.PushError(error.ToString()); await CloseSession(); GetTree().Quit(1); }
    }

    private async Task CaptureUi(string name)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (DisplayServer.GetName() == "headless") { return; }
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var size = DisplayServer.WindowGetSize();
        var result = GetViewport().GetTexture().GetImage().SavePng(ProjectSettings.GlobalizePath($"res://artifacts/p9-review2-{size.X}x{size.Y}-{name}.png"));
        if (result != Error.Ok) { throw new IOException("Unable to save UI review screenshot: " + result); }
    }

    private async Task CheckEtherPresentation()
    {
        // A view-only fixture makes the two sides unambiguous without altering authoritative state.
        var live = _session!.Client.Store.View!;
        var field = _catalog.Cards.First(card => card.Kind == "Field");
        var reference = live.Entities.First();
        var visual = live with
        {
            Lanes = live.Lanes.Select(lane => lane with
            {
                Frozen = lane.LaneId == 0,
                Locked = lane.LaneId is 0 or 1,
                PlayerOne = lane.PlayerOne with { EtherActivation = lane.LaneId == 0 ? 3 : 0 },
                PlayerTwo = lane.PlayerTwo with { EtherActivation = lane.LaneId == 1 ? 2 : 0 }
            }).ToImmutableArray(),
            Entities = live.Entities.Add(reference with
            {
                EntityId = 10001,
                OwnerId = 1,
                ControllerId = 1,
                LaneId = 1,
                Card = reference.Card with { CardInstanceId = 10001 },
                CurrentHealth = 1
            }).Add(reference with
            {
                EntityId = 10002,
                OwnerId = 0,
                ControllerId = 0,
                LaneId = 0,
                Card = new CardView(10002, field.Id, "Field", field.Cost),
                FieldEnergy = 3,
                Attack = null,
                CurrentHealth = null,
                MaximumHealth = null
            }).Add(reference with
            {
                EntityId = 10003,
                OwnerId = 1,
                ControllerId = 1,
                LaneId = 1,
                Card = new CardView(10003, field.Id, "Field", field.Cost),
                FieldEnergy = 2,
                Attack = null,
                CurrentHealth = null,
                MaximumHealth = null
            })
        };
        BuildBattle(visual); _paused = true;
        if (_etherPips[(0, 0)].VisiblePipCount != 3 || _etherPips[(1, 1)].VisiblePipCount != 2
            || _etherPips.Count(pair => pair.Value.Visible) != 2
            || _etherPips[(0, 0)].GetParent().GetNode<EtherPips>("PlayerEther") != _etherPips[(0, 0)])
        { throw new InvalidOperationException("Ether levels must draw pips on only their owning side."); }
        await CaptureUi("ether-sides"); CheckLayout();
        _session.SwitchSeat(); await _session.Client.SynchronizeAsync();
        BuildBattle(visual with { Private = _session.Client.Store.View!.Private }); _paused = true;
        if (_etherPips[(0, 0)].GetParent().GetNode<EtherPips>("OpponentEther") != _etherPips[(0, 0)])
        { throw new InvalidOperationException("Ether presentation must follow the observer seat."); }
        _session.SwitchSeat(); await _session.Client.SynchronizeAsync(); BuildBattle();
    }

    private void CheckLayout()
    {
        var board = _battleScreen.GetNode<ScrollContainer>("BoardScroll").GetGlobalRect();
        var handScroll = _battleScreen.GetNode<ScrollContainer>("HandScroll");
        var hand = handScroll.GetGlobalRect();
        if (board.Intersects(hand) || _submit.GetGlobalRect().Intersects(hand))
        { throw new InvalidOperationException("Battle regions overlap."); }
        var scrollbarHeight = handScroll.GetHScrollBar().IsVisibleInTree() ? handScroll.GetHScrollBar().Size.Y : 0;
        if (_hand.GetChildren().OfType<CardTile>().Any(tile => tile.Size.Y > hand.Size.Y - scrollbarHeight))
        { throw new InvalidOperationException("Hand cards are clipped by the scrollbar."); }
        foreach (var tile in _registry.Values)
        {
            var slot = ((Control)tile.GetParent()).GetGlobalRect();
            if (tile.Size.X > slot.Size.X + 1 || tile.Size.Y > slot.Size.Y + 1)
            { throw new InvalidOperationException($"Card exceeds slot: {tile.Size} / {slot.Size}"); }
        }
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (var child in node.GetChildren())
        { yield return child; foreach (var descendant in Descendants(child)) { yield return descendant; } }
    }
}
