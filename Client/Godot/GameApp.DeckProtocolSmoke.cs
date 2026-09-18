using Eota.Client.Desktop;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private async Task DeckProtocolSmokeAsync()
    {
        var protocolPath = ProjectSettings.GlobalizePath("user://protocol.json");
        var savedProtocol = File.Exists(protocolPath) ? File.ReadAllBytes(protocolPath) : null;
        try
        {
            _protocol = DesktopProtocol.Default;
            _deckFiles.Clear(); _decks.Clear(); _workshopDrafts.Clear();
            _deckStore = new DesktopDeckStore(ProjectSettings.GlobalizePath("res://artifacts/DeckUi-" + Guid.NewGuid().ToString("N")));
            foreach (var profession in Professions) { _decks.Add(_catalog.DefaultDeck(profession)); }
            var saved = _catalog.DefaultDeck("Guardian") with { Name = "删除验证牌组" };
            var id = _deckStore.Save(saved); _decks.Add(saved); _deckFiles.Add(saved, id);
            BuildMenu(); Tabs().CurrentTab = 4; await CaptureInteraction("p10-deck-delete");
            var remove = SavedTile().GetNode<Button>("Delete");
            Require(remove.Visible && !Tiles().First().GetNode<Button>("Delete").Visible, "Saved decks have delete, built-in templates stay available");
            await Click(remove);
            var dialog = Page().GetNode<ConfirmationDialog>("DeleteDeckDialog");
            Require(dialog.Visible && Page().GetNode<Control>("Manager").Visible, "Delete click opens confirmation without entering editor");
            dialog.GetCancelButton().EmitSignal(BaseButton.SignalName.Pressed); await Frames();
            Require(_deckStore.Ids.Contains(id), "Cancel preserves the saved deck");
            await Click(SavedTile().GetNode<Button>("Delete"));
            dialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed); await Frames();
            Require(!_deckStore.Ids.Contains(id) && _decks.Count == Professions.Length && _deckFiles.Count == 0, "Confirmed deletion removes file and selectors");

            Page().GetNode<Button>("Manager/New").EmitSignal(BaseButton.SignalName.Pressed);
            foreach (var profession in Professions)
            {
                Page().GetNode<Button>("ProfessionPicker/Panel/Professions/" + profession).EmitSignal(BaseButton.SignalName.Pressed);
                Require(Page().GetNode<OptionButton>("ProfessionPicker/Panel/Template").ItemCount == 4, "Each profession offers a blank deck and three archetypes");
            }
            Page().GetNode<Button>("ProfessionPicker/Panel/Professions/Guardian").EmitSignal(BaseButton.SignalName.Pressed);
            var template = Page().GetNode<OptionButton>("ProfessionPicker/Panel/Template");
            await Click(template); await CaptureInteraction("cardset-archetypes"); template.GetPopup().Hide();
            template.Select(3);
            await Click(Page().GetNode<Button>("ProfessionPicker/Panel/Create"));
            Require(_workshopDrafts.Values.Single().Count == 40 && _workshopDrafts.Values.Single().Name == "火器营", "Selected archetype opens as a complete editable draft");
            Page().GetNode<Button>("Editor/Back").EmitSignal(BaseButton.SignalName.Pressed); await Frames();
            Require(_workshopDrafts.Count == 1, "New deck is retained as a draft");
            await Click(Tiles().Last().GetNode<Button>("Delete"));
            Page().GetNode<ConfirmationDialog>("DeleteDeckDialog").GetOkButton().EmitSignal(BaseButton.SignalName.Pressed); await Frames();
            Require(_workshopDrafts.Count == 0, "Unsaved draft can also be deleted");

            Tabs().CurrentTab = 5;
            var groups = Tabs().GetNode<TabContainer>("Protocol/Groups"); groups.CurrentTab = 2;
            var fields = _protocol.Fields.Where(field => field.Group == "构筑与疲劳").ToArray();
            var rows = groups.GetChild<ScrollContainer>(2).GetNode<VBoxContainer>("Rows").GetChildren().OfType<HBoxContainer>().ToArray();
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i]; var row = rows[i];
                if (field.Key == "requiredDeckSize") { row.GetNode<LineEdit>("Number").Text = "24"; }
                if (field.Key == "minCopiesPerCard") { row.GetNode<LineEdit>("Number").Text = "2"; }
                if (field.Key == "maxCopiesPerCard") { row.GetNode<LineEdit>("Number").Text = "4"; }
                if (field.Kind == "choice")
                { Require(row.GetNode<OptionButton>("Choice").ItemCount == 3, "Three visible construction/fatigue policies"); row.GetNode<OptionButton>("Choice").Select(1); }
            }
            Tabs().GetNode<Button>("Protocol/Actions/Apply").EmitSignal(BaseButton.SignalName.Pressed); await Frames();
            Require(_protocol.RequiredDeckSize == 24 && _protocol.MinCopies == 2 && _protocol.MaxCopies == 4, "Protocol fields apply");
            Require(DesktopProtocol.Parse(File.ReadAllText(protocolPath)).Summary == _protocol.Summary, "Protocol persists");
            Tabs().GetNode<TabContainer>("Protocol/Groups").CurrentTab = 2;
            await CaptureInteraction("p10-deck-protocol");
            Tabs().CurrentTab = 4; Tiles().First().EmitSignal(BaseButton.SignalName.Pressed); await Frames();
            Require(Page().GetNode<GridContainer>("Editor/Scroll/Grid").GetChildren().OfType<CardTile>().All(card => card.Prototype.Profession == "Guardian"), "Profession-only protocol automatically filters cards");
            Require(Page().GetNode<Label>("Editor/Count").Text.StartsWith("24 / 24", StringComparison.Ordinal), "Configured deck size shown");
            var deckRows = Page().GetNode<VBoxContainer>("Editor/DeckScroll/Rows").GetChildren().OfType<HBoxContainer>()
                .Select(row => row.GetNode<Label>("Cost").Text).ToArray();
            Require(deckRows.Length == _decks.First().Cards.Length, "Deck rows list every selected copy group");
            Require(deckRows.All(cost => long.TryParse(cost, out _)), "Every deck row shows a cost");
            Require(deckRows.Select(long.Parse).SequenceEqual(deckRows.Select(long.Parse).Order()), "Deck rows are listed by cost");
            Page().GetNode<LineEdit>("Editor/DeckName").Text = "24 张守卫牌组";
            await CaptureInteraction("p10-deck-construction");
            Page().GetNode<Button>("Editor/Save").EmitSignal(BaseButton.SignalName.Pressed); await Frames();
            Require(_deckStore.Ids.Length == 1 && _decks.Last().Cards.Sum(card => card.Copies) == 24, "Custom deck saves using protocol limits");
            Page().GetNode<Button>("Manager/New").EmitSignal(BaseButton.SignalName.Pressed);
            Require(Page().GetNode<OptionButton>("ProfessionPicker/Panel/Template").ItemCount == 1, "Incompatible forty-card templates are filtered under a twenty-four-card protocol");
            Page().GetNode<Button>("ProfessionPicker/Panel/Cancel").EmitSignal(BaseButton.SignalName.Pressed);
            Tabs().CurrentTab = 2;
            Require(Tabs().GetNodeOrNull<OptionButton>("Host/Two") is null && Tabs().GetNode<Label>("Host/OpponentDeck").Text.Contains("自行选择", StringComparison.Ordinal), "Hosting does not require knowing opponent deck");
            await CaptureInteraction("p10-deck-host");
            GD.Print("P10_DECK_PROTOCOL_SMOKE_OK delete=pointer-and-confirmation draft=removable protocol=saved construction=filtered limits=24/2/4 opponent=self-selected archetypes=15 template=editable");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
        finally
        {
            if (savedProtocol is not null) { File.WriteAllBytes(protocolPath, savedProtocol); }
            else if (File.Exists(protocolPath)) { File.Delete(protocolPath); }
        }

        TabContainer Tabs() => _menuScreen.GetNode<TabContainer>("Tabs");
        Control Page() => Tabs().GetNode<Control>("DeckBuilder");
        Button[] Tiles() => Page().GetNode<GridContainer>("Manager/Scroll/Grid").GetChildren().OfType<Button>().ToArray();
        Button SavedTile() => Tiles().Single(tile => tile.GetNode<Label>("Name").Text == "删除验证牌组");
        async Task Frames() { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Click(Control control)
        {
            var position = control.GetGlobalRect().GetCenter();
            GetViewport().PushInput(new InputEventMouseMotion { Position = position, GlobalPosition = position }, true); await Frames();
            foreach (var pressed in new[] { true, false })
            { GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = position, GlobalPosition = position }, true); }
            await Frames();
        }
    }
}
