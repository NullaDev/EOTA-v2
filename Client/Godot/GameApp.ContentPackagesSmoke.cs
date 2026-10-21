using Eota.Client.Desktop;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private async Task ContentPackagesSmokeAsync()
    {
        try
        {
            var directory = ProjectSettings.GlobalizePath("res://artifacts/ContentPackages-" + Guid.NewGuid().ToString("N"));
            _packageStore = new DesktopContentPackages(_catalog.Root, Path.Combine(directory, "content"));
            _deckStore = new DesktopDeckStore(Path.Combine(directory, "decks"));
            _deckFiles.Clear(); _decks.Clear(); _workshopDrafts.Clear();
            var editor = DesktopContentEditor.NewPackage(_catalog); editor.PackageId = "smoke-extra";
            var extra = editor.SaveCard(DesktopContentEditor.NewCard());
            var path = Path.Combine(directory, "extra.json"); editor.SavePack(path); _packageStore.Install(path);
            _packageStore.SaveSelection(["standard", "smoke-extra"]); _reloadPackages = true; BuildMenu();
            var original = _catalog.DefaultDeck("Guardian");
            var saved = original with { Name = "保留缺牌测试", Cards = [new(extra, original.Cards[0].Copies), .. original.Cards.Skip(1)] };
            var id = _deckStore.Save(saved); _decks.Add(saved); _deckFiles.Add(saved, id);
            var bytes = File.ReadAllBytes(Path.Combine(directory, "decks", id + ".json"));
            var draft = new DesktopDeckDraft(_catalog, "Guardian"); draft.Load(saved); _workshopDrafts.Add("saved:" + id, draft);
            BuildMenu();
            await Frames();
            await Click(_menuScreen.GetNode<Button>("Navigation/More"));
            await Click(_menuScreen.GetNode<Button>("Navigation/Editor"));
            Require(Tabs().CurrentTab == 8, "Pointer opens card editor through More menu");
            await Click(_menuScreen.GetNode<Button>("Navigation/More"));
            Require(_menuScreen.GetNode<Button>("Navigation/ContentPacks").Visible, "Menu exposes package manager");
            await Click(_menuScreen.GetNode<Button>("Navigation/ContentPacks"));
            Require(_packageWindow is { Visible: true }, "Pointer opens content packages through More menu");
            await CaptureInteraction("content-pack-manager");
            Select("smoke-extra", false); Save(); await Frames();
            Require(_catalog.Cards.Length == 282 && new DesktopContentPackages(_catalog.Root, _packageStore.UserDirectory).ReadSelection().SequenceEqual(["standard"]),
                "Disabled package remains disabled after settings reload");
            Require(File.ReadAllBytes(Path.Combine(directory, "decks", id + ".json")).SequenceEqual(bytes), "Disabling package never changes deck file");
            Tabs().CurrentTab = 4;
            var tile = Page().GetNode<GridContainer>("Manager/Scroll/Grid").GetChildren().OfType<Button>().Single(t => t.GetNode<Label>("Name").Text == saved.Name);
            tile.EmitSignal(BaseButton.SignalName.Pressed); await Frames();
            Require(Page().GetNode<Control>("Manager").Visible && !Page().GetNode<Control>("Editor").Visible, "Missing cards block entering deck editor");
            Require(tile.GetNode<Button>("Delete").Visible && _workshopDrafts["saved:" + id].Copies(extra) > 0, "Manual deletion stays available and draft keeps missing cards");
            Require(Tabs().GetNode<OptionButton>("Ai/One").IsItemDisabled(_decks.IndexOf(saved)), "Missing deck cannot be selected for play");
            await CaptureInteraction("content-pack-missing-deck");
            ShowContentPackages(); Select("smoke-extra", true); Save(); await Frames();
            Tabs().CurrentTab = 4;
            Page().GetNode<GridContainer>("Manager/Scroll/Grid").GetChildren().OfType<Button>().Single(t => t.GetNode<Label>("Name").Text == saved.Name)
                .EmitSignal(BaseButton.SignalName.Pressed); await Frames();
            Require(Page().GetNode<Control>("Editor").Visible && draft.Copies(extra) > 0, "Re-enabling restores editing and draft");
            _session = await DesktopSession.LocalAsync(_catalog, saved, _catalog.DefaultDeck("Hunter"), new());
            BuildBattle(); var activeCatalog = _catalog;
            ShowContentPackages(); Select("smoke-extra", false); Save(); await Frames();
            Require(ReferenceEquals(_catalog, activeCatalog) && _session is not null && _battle, "In-match save preserves active rules");
            await CloseSession(); BuildMenu(); await Frames();
            Require(_catalog.Cards.Length == 282 && _deckStore.Ids.Contains(id), "Lobby applies new selection without losing deck");
            ShowContentPackages(); Select("standard", false); Save(); await Frames();
            Require(_reloadPackages && Descendants(_root).OfType<Label>().Any(l => l.Text == "内容包尚未就绪"), "Empty selection blocks gameplay with repair screen");
            await CaptureInteraction("content-pack-recovery");
            ShowContentPackages(); Select("standard", true); Save(); await Frames();
            Require(!_reloadPackages && _catalog.Cards.Length == 282 && _deckStore.Ids.Contains(id), "Repair reloads content and retains unavailable deck");
            ShowContentPackMismatch("content-pack-mismatch\nstandard：主机版本 2.0.0 / 本机版本 1.1.2-pre-release（hash 不同）");
            await CaptureInteraction("content-pack-version-mismatch");
            var mismatch = GetNode<Window>("ContentPackMismatch");
            Require(Descendants(mismatch).OfType<RichTextLabel>().Single().Text.Contains("主机版本 2.0.0 / 本机版本 1.1.2-pre-release", StringComparison.Ordinal),
                "Version mismatch is displayed in a scrollable dialog");
            mismatch.QueueFree();
            GD.Print("CONTENT_PACK_SMOKE_OK manager=available persistence=verified deck=preserved-and-locked restore=editable match=frozen conflicts=repairable");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); await CloseSession(); GetTree().Quit(1); }

        TabContainer Tabs() => _menuScreen.GetNode<TabContainer>("Tabs");
        Control Page() => Tabs().GetNode<Control>("DeckBuilder");
        void Select(string id, bool on) => Descendants(_packageWindow!).OfType<CheckBox>().Single(c => c.Name == "Pack_" + id).ButtonPressed = on;
        void Save() => Descendants(_packageWindow!).OfType<Button>().Single(b => b.Name == "SaveSelection").EmitSignal(BaseButton.SignalName.Pressed);
        async Task Frames() { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Click(Control control)
        {
            var point = control.GetGlobalRect().GetCenter();
            GetViewport().PushInput(new InputEventMouseMotion { Position = point, GlobalPosition = point }, true);
            await Frames();
            foreach (var down in new[] { true, false })
            { GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = down, Position = point, GlobalPosition = point }, true); }
            await Frames();
        }
    }
}
