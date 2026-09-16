using Eota.Client.Desktop;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private static readonly string[] Professions = ["Guardian", "Arcanist", "Artisan", "Hunter"];
    private string _deckProfession = "Guardian";

    private void BuildCollection(TabContainer tabs)
    {
        var page = tabs.GetNode<Control>("Library");
        var search = page.GetNode<LineEdit>("Search");
        var profession = page.GetNode<OptionButton>("Profession");
        profession.AddItem("全部职业"); foreach (var value in Professions) { profession.AddItem(Ui.Profession(value)); }
        profession.AddItem("中立");
        var grid = page.GetNode<GridContainer>("Scroll/Grid");
        var detail = page.GetNode<VBoxContainer>("Detail");
        var related = page.GetNode<Control>("Related");
        var thumbnails = related.GetNode<HBoxContainer>("Scroll/Cards");
        var back = related.GetNode<Button>("Back");
        CardTile? pinned = null;
        CardTile? hovered = null;
        void ClearSelection()
        {
            if (GodotObject.IsInstanceValid(pinned)) { pinned!.Highlight(false); }
            pinned = null; hovered = null; Ui.Clear(detail); related.Hide(); Ui.Clear(thumbnails);
        }
        void ShowCard(CardPresentation card)
        {
            Ui.Clear(detail); detail.AddChild(CardTile.CreatePrototype(card, true));
            detail.AddChild(Ui.Text(Ui.Profession(card.Profession) + (card.Source == "Token" ? " · 衍生卡" : " · 基础卡")));
        }
        void ShowRelated(CardPresentation original)
        {
            var children = _catalog.RelatedCards(original.Id); Ui.Clear(thumbnails);
            related.Visible = !children.IsEmpty; back.Hide();
            foreach (var child in children)
            {
                var tile = CardTile.CreateThumbnail(child); thumbnails.AddChild(tile); tile.TooltipText = child.Name;
                tile.PrototypeClicked = value =>
                {
                    ShowCard(value); back.Show();
                    foreach (var thumbnail in thumbnails.GetChildren().OfType<CardTile>()) { thumbnail.Highlight(thumbnail == tile); }
                };
            }
        }
        back.Pressed += () =>
        {
            if (pinned is null) { return; }
            ShowCard(pinned.Prototype); back.Hide();
            foreach (var thumbnail in thumbnails.GetChildren().OfType<CardTile>()) { thumbnail.Highlight(false); }
        };
        void Refresh()
        {
            ClearSelection(); Ui.Clear(grid);
            var filter = profession.Selected == 0 ? null : profession.Selected == 5 ? "Neutral" : Professions[profession.Selected - 1];
            foreach (var card in _catalog.Cards.Where(card => card.Source == "Core" && (filter is null || card.Profession == filter) && MatchesSearch(card, search.Text)))
            {
                var tile = CardTile.CreatePrototype(card); tile.TooltipText = ""; grid.AddChild(tile);
                tile.Inspected = value => { if (pinned is null) { hovered = tile; ShowCard(value); } };
                tile.InspectionEnded = () => { if (pinned is null && hovered == tile) { hovered = null; Ui.Clear(detail); } };
                tile.PrototypeClicked = value =>
                {
                    if (pinned == tile) { ClearSelection(); return; }
                    ClearSelection(); pinned = tile; tile.Highlight(true); ShowCard(value); ShowRelated(value);
                };
            }
            page.GetNode<Label>("Count").Text = $"{grid.GetChildCount()} 张卡牌";
        }
        page.VisibilityChanged += () => { if (!page.IsVisibleInTree()) { ClearSelection(); } };
        search.TextChanged += _ => Refresh(); profession.ItemSelected += _ => Refresh(); Refresh();
    }

    private static bool MatchesSearch(CardPresentation card, string search) => string.IsNullOrWhiteSpace(search)
        || (card.Name + card.Description + card.Id).Contains(search, StringComparison.OrdinalIgnoreCase);
}
