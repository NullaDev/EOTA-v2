using Eota.Client.Desktop;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private async Task TutorialSmokeAsync()
    {
        try
        {
            var root = _packageStore.Root;
            var user = ProjectSettings.GlobalizePath("user://tutorial-smoke/" + Guid.NewGuid().ToString("N"));
            _packageStore = new DesktopContentPackages(root, Path.Combine(user, "content"));
            _deckStore = new DesktopDeckStore(Path.Combine(user, "decks"));
            _decks.Clear(); _deckFiles.Clear(); _workshopDrafts.Clear(); _contentEditor = null; _editorSnapshot = null;
            _packageStore.SaveSelection(["tutorial"]); _reloadPackages = true; BuildMenu();
            Require(!_reloadPackages && _catalog.Packages.Length == 1 && _catalog.Packages[0].Id == "tutorial", "Tutorial loads independently");
            Require(Professions.SequenceEqual(["Wayfarer"]) && Ui.Profession("Wayfarer") == "行旅者", "Custom profession is populated from package");
            foreach (var name in new[] { "ether", "frozen", "locked", "fast-spell", "slow-spell", "slow-minion" }) { Ui.Icon(name).Free(); }
            var tabs = _menuScreen.GetNode<TabContainer>("Tabs"); tabs.CurrentTab = 3;
            await CaptureInteraction("tutorial-library");
            tabs.CurrentTab = 4;
            var workshop = tabs.GetNode<Control>("DeckBuilder");
            workshop.GetNode<Button>("Manager/New").EmitSignal(BaseButton.SignalName.Pressed);
            Require(workshop.GetNode<Label>("ProfessionPicker/Panel/Description").Text.Contains("不依赖 standard", StringComparison.Ordinal), "Custom introduction is shown");
            await CaptureInteraction("tutorial-profession");
            workshop.GetNode<OptionButton>("ProfessionPicker/Panel/Template").Select(1);
            workshop.GetNode<Button>("ProfessionPicker/Panel/Create").EmitSignal(BaseButton.SignalName.Pressed);
            Require(workshop.GetNode<Control>("Editor").Visible, "Custom profession deck is editable");
            var deck = _catalog.DefaultDeck("Wayfarer");
            Require(deck.Cards.Sum(c => c.Copies) == 40 && _catalog.ValidateDeck(deck).IsEmpty, "Tutorial deck follows default rules");
            _session = await DesktopSession.LocalAsync(_catalog, deck, deck, new(Mulligan: false)); BuildBattle();
            await CaptureInteraction("tutorial-battle");
            Require(_battle && _session.Client.Store.View is not null, "Tutorial match runs with generic UI resources");
            await CloseSession();
            GD.Print("TUTORIAL_SMOKE_OK packages=tutorial professions=Wayfarer cards=15 deck=40 library=visible editor=editable battle=running ui=independent");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); await CloseSession(); GetTree().Quit(1); }
    }
}
