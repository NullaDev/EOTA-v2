using System.Collections.Immutable;
using Eota.Client.Desktop;
using Eota.Transport.Contracts;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private async Task InteractionSmokeAsync()
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var tabs = _menuScreen.GetNode<TabContainer>("Tabs");
            var ai = tabs.GetNode<Control>("Ai"); var local = tabs.GetNode<Control>("Local");
            Require(ai.GetNode<OptionButton>("Difficulty").ItemCount == 3 && local.GetNodeOrNull<OptionButton>("Difficulty") is null, "Separate setup pages");
            Require(_menuScreen.GetNodeOrNull<Label>("Footnote") is null && _menuScreen.GetNodeOrNull<Label>("Edition") is null, "Remove development copy");
            tabs.CurrentTab = ai.GetIndex(); await CaptureInteraction("ai-setup");
            _menuScreen.GetNode<Button>("Navigation/Local").EmitSignal(BaseButton.SignalName.Pressed);
            Require(tabs.CurrentTab == local.GetIndex(), "Local navigation"); await CaptureInteraction("local-setup");
            local.GetNode<Button>("Protocol").EmitSignal(BaseButton.SignalName.Pressed);
            var groups = tabs.GetNode<TabContainer>("Protocol/Groups");
            Require(groups.GetTabCount() == 3, "Player-facing protocol groups");
            await Frames();
            var bar = groups.GetTabBar();
            var rectangles = Enumerable.Range(0, bar.TabCount).Select(bar.GetTabRect).ToArray();
            foreach (var index in Enumerable.Range(0, bar.TabCount))
            {
                await MovePointer(bar.GlobalPosition + rectangles[index].GetCenter());
                Require(rectangles.SequenceEqual(Enumerable.Range(0, bar.TabCount).Select(bar.GetTabRect)), "Tab hover must keep geometry");
                var widths = new[] { "tab_selected", "tab_unselected", "tab_hovered", "tab_disabled" }.Select(name => bar.GetThemeStylebox(name).GetContentMargin(Side.Left));
                Require(widths.Distinct().Count() == 1, "Tab text margins must agree");
            }
            await CaptureInteraction("protocol");
            tabs.GetNode<Button>("Protocol/Actions/Back").EmitSignal(BaseButton.SignalName.Pressed);
            Require(tabs.CurrentTab == local.GetIndex(), "Protocol returns to the originating setup");

            tabs.CurrentTab = 3; await Frames();
            var library = tabs.GetNode<Control>("Library"); var detail = library.GetNode<VBoxContainer>("Detail");
            var tiles = library.GetNode<GridContainer>("Scroll/Grid").GetChildren().OfType<CardTile>().Take(2).ToArray();
            await MovePointer(tiles[0].GetGlobalRect().GetCenter());
            Require(ShownCard() == tiles[0].Prototype.Id, "Hover shows card");
            await MovePointer(library.GetNode<Label>("Title").GetGlobalRect().GetCenter());
            Require(ShownCard() is null, "Exit clears hover");
            await ClickTile(tiles[0]);
            Require(tiles[0].IsHighlighted && ShownCard() == tiles[0].Prototype.Id, "Click pins card and outside outline");
            await MovePointer(tiles[1].GetGlobalRect().GetCenter());
            Require(ShownCard() == tiles[0].Prototype.Id, "Pinned card ignores other hover");
            await CaptureInteraction("library-pinned");
            await ClickTile(tiles[0]);
            Require(!tiles[0].IsHighlighted && ShownCard() is null, "Second click clears pin even under pointer");
            await MovePointer(tiles[1].GetGlobalRect().GetCenter());
            Require(ShownCard() == tiles[1].Prototype.Id, "Hover resumes after unpin");
            await ClickTile(tiles[1]);
            library.GetNode<LineEdit>("Search").Text = "no-such-card";
            library.GetNode<LineEdit>("Search").EmitSignal(LineEdit.SignalName.TextChanged, "no-such-card"); await Frames();
            Require(ShownCard() is null, "Filtering clears stale details");
            var origin = _catalog.Cards.First(card => card.Source == "Core" && _catalog.RelatedCards(card.Id).Length > 0);
            library.GetNode<LineEdit>("Search").Text = origin.Id;
            library.GetNode<LineEdit>("Search").EmitSignal(LineEdit.SignalName.TextChanged, origin.Id); await Frames();
            var source = library.GetNode<GridContainer>("Scroll/Grid").GetChildren().OfType<CardTile>().Single(tile => tile.Prototype.Id == origin.Id);
            await ClickTile(source);
            var related = library.GetNode<Control>("Related");
            var child = related.GetNode<HBoxContainer>("Scroll/Cards").GetChildren().OfType<CardTile>().First();
            Require(related.Visible && child.Prototype.Source == "Token", "Derived cards are only related thumbnails");
            await ClickTile(child);
            Require(ShownCard() == child.Prototype.Id && source.IsHighlighted && child.IsHighlighted, "Derived thumbnail switches detail and preserves origin");
            await CaptureInteraction("derived-card");
            related.GetNode<Button>("Back").EmitSignal(BaseButton.SignalName.Pressed);
            Require(ShownCard() == origin.Id, "Return from derived card");
            await ClickTile(source); Require(!related.Visible && ShownCard() is null, "Unpin clears related cards");
            foreach (var card in _catalog.Cards.Where(card => card.Kind == "Field"))
            {
                var tile = CardTile.CreatePrototype(card);
                Require(tile.GetNode<Control>("HealthBox").Visible == (card.Durability is not null), "Only permanent fields omit durability");
                if (card.Durability is { } durability) { Require(tile.GetNode<Label>("HealthBox/Value").Text == durability.ToString(), "Prototype durability value"); }
                tile.Free();
            }
            var permanent = CardTile.CreatePrototype(_catalog.Cards.First(card => card.Kind == "Field") with { Durability = null });
            Require(!permanent.GetNode<Control>("HealthBox").Visible, "Permanent prototype has no durability badge"); permanent.Free();

            // A legal custom protocol deals both whole decks so the UI scenarios can use real
            // commands for minions, fields, all four spell categories and replacements.
            _protocol = DesktopProtocol.Default.WithValues(new Dictionary<string, string>
            {
                ["openingHandSize"] = "40",
                ["handLimit"] = "40",
                ["initialPlayerCost"] = "100",
                ["initialMaxCost"] = "100",
                ["maxCostLimit"] = "100",
                ["mulliganEnabled"] = "false"
            });
            BuildMenu(); tabs = _menuScreen.GetNode<TabContainer>("Tabs"); tabs.CurrentTab = 0;
            tabs.GetNode<Button>("Local/Start").EmitSignal(BaseButton.SignalName.Pressed);
            await Until(() => _session is not null && !_busy);
            var session = _session!; Require(session.AiSettings is null && session.CanSwitchSeat, "Local mode does not start AI");
            _paused = true;
            var minion = session.Client.Store.View!.Private!.Hand.First(card => card.CardKind == "Minion" && card.Keywords.Any(keyword => keyword.Kind == "Replaceable"));
            var field = session.Client.Store.View.Private.Hand.First(card => card.CardKind == "Field");
            var minionPlan = await Drop(minion, 0); var fieldPlan = await Drop(field, 0);
            foreach (var speed in new[] { "Fast", "Slow" })
            {
                foreach (var global in new[] { false, true })
                {
                    var card = session.Client.Store.View!.Private!.Hand.First(card => Presentation(card) is { Kind: "Spell" } prototype && prototype.Speed == speed && prototype.Global == global);
                    await Drop(card, global ? null : 1);
                }
            }
            Require(_plannedTiles.Count == 2 && _plannedTiles.Values.All(tile => tile.IsHighlighted), "Planned minion and field outlines");
            Require(_registry.Count == 0, "Previews must not be authoritative entities");
            Require(_laneSpellPlans[1].FastCount == 1 && _laneSpellPlans[1].SlowCount == 1 && _globalSpellPlans.FastCount == 1 && _globalSpellPlans.SlowCount == 1, "Fast and slow lane/global markers");
            CheckLayout(); await CaptureInteraction("planned");
            await MovePointer(_plannedTiles[minionPlan].GetGlobalRect().GetCenter());
            Require(_detail.GetChildCount() == 1, "Battle hover opens preview");
            await MovePointer(_matchInfo.GetGlobalRect().GetCenter());
            Require(_detail.GetChildCount() == 0, "Battle exit clears preview without entering another card");
            await MovePointer(_laneSpellPlans[1].GetNode<HBoxContainer>("Scroll/Icons").GetChild<TextureButton>(0).GetGlobalRect().GetCenter());
            Require(_detail.GetChildCount() == 1, "Spell marker hover opens preview");
            await MovePointer(_matchInfo.GetGlobalRect().GetCenter());
            Require(_detail.GetChildCount() == 0, "Spell marker exit clears preview");

            FloatText("private-animation-test", new Vector2(600, 400), Colors.White);
            var switchRect = _battleScreen.GetNode<Button>("Toolbar/SwitchSeat").GetGlobalRect();
            _battleScreen.GetNode<Button>("Toolbar/SwitchSeat").EmitSignal(BaseButton.SignalName.Pressed);
            Require(!_battle && session.Client.Store.View!.Private!.PlayerId == 0 && !Descendants(_root).OfType<CardTile>().Any(), "Switch first removes hands without changing observer");
            Require(!Descendants(this).OfType<Label>().Any(label => label.IsInsideTree() && label.Text == "private-animation-test"), "Handoff removes battle animations");
            var handoff = _root.GetNode<Control>("SeatHandoff");
            Require(!switchRect.Intersects(handoff.GetNode<Button>("Continue").GetGlobalRect()), "Original double click cannot reveal next hand");
            await CaptureInteraction("handoff");
            await ContinueHandoff();
            Require(session.Client.Store.View!.Private!.PlayerId == 1 && _plannedTiles.Count == 0 && _globalSpellPlans.FastCount == 0
                && _laneSpellPlans.Values.All(marker => marker.FastCount + marker.SlowCount == 0), "Other player sees no private plans");
            _battleScreen.GetNode<Button>("Toolbar/SwitchSeat").EmitSignal(BaseButton.SignalName.Pressed);
            await ContinueHandoff();
            Require(session.Client.Store.View!.Private!.PlayerId == 0 && _plannedTiles.Count == 2 && _laneSpellPlans[1].FastCount == 1, "Own plans restore after handoff");

            _plannedTiles[minionPlan].Clicked!(minion.CardInstanceId); await Until(() => !_busy);
            Require(!_plannedTiles.ContainsKey(minionPlan) && session.Client.Store.View!.Private!.Hand.Any(card => card.CardInstanceId == minion.CardInstanceId), "Preview click cancels and restores hand");
            CancelPlan(fieldPlan); await Until(() => !_busy);
            Require(_plannedTiles.Count == 0, "Field cancel clears preview");
            foreach (var plan in session.Client.Store.View!.Private!.Planning.ToArray()) { CancelPlan(plan.PlanCommandId); await Until(() => !_busy); }
            Require(_laneSpellPlans.Values.All(marker => marker.FastCount + marker.SlowCount == 0) && _globalSpellPlans.FastCount + _globalSpellPlans.SlowCount == 0, "Spell cancel clears markers");

            var groupsToCancel = new List<(SpellPlans Markers, int? Lane, List<(CardView Card, ulong Id)> Plans)>();
            foreach (var target in new int?[] { 2, null })
            {
                var candidates = session.Client.Store.View!.Private!.Hand.Where(card => Presentation(card) is { Kind: "Spell", Speed: "Slow" } prototype
                    && prototype.Global == (target is null)).Take(3).ToArray();
                Require(candidates.Length == 3, "Fixture has three slow spells for each scope");
                var ids = new List<(CardView, ulong)>();
                foreach (var card in candidates) { ids.Add((card, await Drop(card, target))); }
                var markers = target is { } lane ? _laneSpellPlans[lane] : _globalSpellPlans;
                Require(markers.GetNode<HBoxContainer>("Scroll/Icons").GetChildCount() == 3 && markers.SlowCount == 3, "Three spells render three hourglasses");
                groupsToCancel.Add((markers, target, ids));
            }
            await CaptureInteraction("three-spells");
            foreach (var group in groupsToCancel)
            {
                var selectedPlan = group.Plans[1];
                var before = session.Client.Store.View!.Private!;
                await ClickTile(Marker(group.Markers, selectedPlan.Id)); await Until(() => !_busy);
                var after = session.Client.Store.View!.Private!;
                Require(group.Markers.SlowCount == 2 && after.Planning.Length == before.Planning.Length - 1
                    && after.Planning.All(plan => plan.PlanCommandId != selectedPlan.Id)
                    && group.Plans.Where(plan => plan.Id != selectedPlan.Id).All(plan => after.Planning.Any(value => value.PlanCommandId == plan.Id))
                    && after.Hand.Any(card => card.CardInstanceId == selectedPlan.Card.CardInstanceId)
                    && after.AvailableCost == before.AvailableCost + selectedPlan.Card.Cost, "Clicking middle icon cancels only its own spell and refunds cost");
            }
            var overflowing = groupsToCancel[0];
            await Drop(overflowing.Plans[1].Card, overflowing.Lane);
            var fast = session.Client.Store.View!.Private!.Hand.First(card => Presentation(card) is { Kind: "Spell", Speed: "Fast", Global: false });
            var extraId = await Drop(fast, overflowing.Lane); await Frames();
            var spellScroll = overflowing.Markers.GetNode<ScrollContainer>("Scroll");
            Require(spellScroll.GetHScrollBar().IsVisibleInTree(), "Additional spells stay reachable by scrolling");
            spellScroll.ScrollHorizontal = (int)spellScroll.GetHScrollBar().MaxValue; await Frames();
            Require(Marker(overflowing.Markers, extraId).GetGlobalRect().End.X <= spellScroll.GetGlobalRect().End.X + 1, "Last spell is reachable");
            Require(Marker(overflowing.Markers, extraId).GetGlobalRect().End.Y <= spellScroll.GetGlobalRect().End.Y - spellScroll.GetHScrollBar().Size.Y + 1, "Scrollbar does not clip spell icons");
            await CaptureInteraction("spell-overflow");
            await ClickTile(Marker(overflowing.Markers, extraId)); await Until(() => !_busy);
            foreach (var plan in session.Client.Store.View!.Private!.Planning.ToArray())
            {
                var markers = plan.LaneId is { } lane ? _laneSpellPlans[lane] : _globalSpellPlans;
                await ClickTile(Marker(markers, plan.PlanCommandId)); await Until(() => !_busy);
            }
            Require(session.Client.Store.View!.Private!.Planning.IsEmpty, "All individual spell markers can be cancelled");

            await Drop(minion, 0); await Drop(field, 0);
            var submittedSpell = session.Client.Store.View!.Private!.Hand.First(card => Presentation(card) is { Kind: "Spell", Speed: "Fast", Global: false });
            var submittedId = await Drop(submittedSpell, 1);
            await SubmitForSmoke(new SubmitTurnPayload());
            Render(session.Client.Store.View!); await Frames();
            var blocked = Marker(_laneSpellPlans[1], submittedId);
            Require(blocked.Disabled, "Submitted spell markers cannot cancel");
            await ClickTile(blocked);
            Require(session.Client.Store.View!.Private!.Planning.Any(plan => plan.PlanCommandId == submittedId), "Disabled click preserves submitted spell");
            session.SwitchSeat(); await session.Client.SynchronizeAsync();
            await SubmitForSmoke(new SubmitTurnPayload());
            session.SwitchSeat(); await session.Client.SynchronizeAsync(); BuildBattle();
            Require(_plannedTiles.Count == 0 && session.Client.Store.View!.Turn == 2, "Resolution clears previews");
            var existing = session.Client.Store.View!.Entities.Single(entity => entity.ControllerId == 0 && entity.LaneId == 0 && entity.Card.CardKind == "Minion");
            var liveField = session.Client.Store.View.Entities.First(entity => entity.Card.CardKind == "Field");
            Require(_registry[liveField.EntityId].GetNode<Label>("HealthBox/Value").Text == liveField.FieldEnergy?.ToString(), "Deployed field shows current durability");
            var permanentField = Tile(liveField.Card, true); permanentField.UpdateCard(liveField.Card, liveField with { PermanentField = true, FieldEnergy = null });
            Require(!permanentField.GetNode<Control>("HealthBox").Visible, "Permanent field instance has no badge"); permanentField.Free();
            var replacement = session.Client.Store.View.Private!.Hand.First(card => card.CardKind == "Minion" && session.Client.Store.View.Private.PlanOptions.Any(option => option.CardInstanceId == card.CardInstanceId && option.LaneId == 0 && option.Allowed));
            var replacementPlan = await Drop(replacement, 0);
            Require(!_registry[existing.EntityId].Visible && _plannedTiles[replacementPlan].IsHighlighted, "Replacement preview hides original visually");
            await CaptureInteraction("replacement");
            CancelPlan(replacementPlan); await Until(() => !_busy);
            Require(_registry[existing.EntityId].Visible && _plannedTiles.Count == 0, "Cancel restores existing entity");
            var viewOnly = session.Client.Store.View! with { Private = null, Audience = Audience.Spectator };
            Render(viewOnly); Require(_plannedTiles.Count == 0 && _globalSpellPlans.FastCount + _globalSpellPlans.SlowCount == 0, "Spectator sees no private previews");
            await session.SaveReplayAsync(ProjectSettings.GlobalizePath("res://artifacts/P96InteractionReplay"));
            var replay = DesktopReplay.Load(_catalog, ProjectSettings.GlobalizePath("res://artifacts/P96InteractionReplay/replay.json"));
            Require(replay.StateHash == session.FinalStateHash, "Interaction changes preserve authoritative replay");
            BuildBattle();
            _battleScreen.GetNode<Button>("Toolbar/SwitchSeat").EmitSignal(BaseButton.SignalName.Pressed);
            _root.GetNode<Button>("SeatHandoff/Back").EmitSignal(BaseButton.SignalName.Pressed);
            await Until(() => _session is null && !_busy);
            GD.Print("P96_INTERACTION_SMOKE_OK setup=separate handoff=private planning=live spells=individual middle-cancel=lane-and-global overflow=reachable submitted=blocked cancel=verified library=pointer-tested derived=switchable durability=prototype-and-instance preview=cleared tabs=stable replay=verified");
            GetTree().Quit();

            string? ShownCard() => detail.GetChildren().OfType<CardTile>().FirstOrDefault()?.Prototype.Id;
            TextureButton Marker(SpellPlans markers, ulong id) => markers.GetNode<TextureButton>("Scroll/Icons/Plan" + id);
            async Task ClickTile(Control tile)
            {
                var position = tile.GetGlobalRect().GetCenter(); await MovePointer(position);
                GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = position, GlobalPosition = position }, true);
                GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = position, GlobalPosition = position }, true);
                await Frames();
            }
            async Task MovePointer(Vector2 position)
            {
                GetViewport().PushInput(new InputEventMouseMotion { Position = position, GlobalPosition = position }, true); await Frames();
            }
            async Task Frames() { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            async Task Until(Func<bool> condition)
            { while (!condition()) { deadline.Token.ThrowIfCancellationRequested(); await Frames(); } }
            async Task ContinueHandoff()
            {
                _root.GetNode<Button>("SeatHandoff/Continue").EmitSignal(BaseButton.SignalName.Pressed);
                await Until(() => _battle && !_busy);
            }
            async Task<ulong> Drop(CardView card, int? lane)
            {
                var drop = _drops.Single(drop => drop.LaneId == lane);
                Require(drop._CanDropData(Vector2.Zero, card.CardInstanceId.ToString()), "Card must be legally draggable");
                drop._DropData(Vector2.Zero, card.CardInstanceId.ToString());
                await Until(() => !_busy);
                return session.Client.Store.View!.Private!.Planning.Single(plan => plan.Card.CardInstanceId == card.CardInstanceId).PlanCommandId;
            }
        }
        catch (Exception error) { _busy = false; GD.PushError(error.ToString()); await CloseSession(); GetTree().Quit(1); }
    }

    private static void Require(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }

    private async Task CaptureInteraction(string name)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (DisplayServer.GetName() == "headless") { return; }
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var size = DisplayServer.WindowGetSize();
        var result = GetViewport().GetTexture().GetImage().SavePng(ProjectSettings.GlobalizePath($"res://artifacts/p96-{size.X}x{size.Y}-{name}.png"));
        if (result != Error.Ok) { throw new IOException("Screenshot failed: " + result); }
    }
}
