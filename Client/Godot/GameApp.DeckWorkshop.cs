using Eota.Client.Desktop;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private readonly Dictionary<string, DesktopDeckDraft> _workshopDrafts = new(StringComparer.Ordinal);

    private void BuildDeckBuilder(TabContainer tabs)
    {
        var page = tabs.GetNode<Control>("DeckBuilder");
        var manager = page.GetNode<Control>("Manager"); var editor = page.GetNode<Control>("Editor");
        var picker = page.GetNode<Control>("ProfessionPicker");
        var deckGrid = manager.GetNode<GridContainer>("Scroll/Grid");
        var cardGrid = editor.GetNode<GridContainer>("Scroll/Grid");
        var rows = editor.GetNode<VBoxContainer>("DeckScroll/Rows");
        var name = editor.GetNode<LineEdit>("DeckName"); var search = editor.GetNode<LineEdit>("Search");
        DesktopDeckDraft? current = null; string? currentKey = null;

        void RefreshRows()
        {
            if (current is null) { return; }
            Ui.Clear(rows); editor.GetNode<Label>("Count").Text = $"{current.Count} / {current.Protocol.RequiredDeckSize} 张 · 单卡 {current.Protocol.MinCopies}–{current.Protocol.MaxCopies}";
            // The draft itself stays in canonical ID order; only the display follows the deck curve.
            var prototypes = _catalog.Cards.ToDictionary(card => card.Id, StringComparer.Ordinal);
            var entries = current.Build().Cards
                .OrderBy(entry => prototypes.TryGetValue(entry.Id, out var known) ? known.Cost : long.MaxValue)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                prototypes.TryGetValue(entry.Id, out var card);
                var row = Ui.Instantiate<HBoxContainer>("DeckRow"); rows.AddChild(row);
                row.GetNode<Label>("Name").Text = card?.Name ?? entry.Id; row.GetNode<Label>("Cost").Text = card?.Cost.ToString() ?? "?";
                var valid = current.Pool.Any(value => value.Id == entry.Id) && entry.Copies >= current.Protocol.MinCopies && entry.Copies <= current.Protocol.MaxCopies;
                row.GetNode<Label>("Copies").Text = "× " + entry.Copies; row.TooltipText = valid ? card?.Description ?? "" : "此卡或数量不符合当前协议，请调整或移除。";
                if (!valid) { row.Modulate = new Color(1, 0.65f, 0.6f); }
                row.GetNode<Button>("Remove").Pressed += () => { current.Remove(entry.Id); RefreshRows(); };
            }
            foreach (var tile in cardGrid.GetChildren().OfType<CardTile>())
            { tile.Modulate = current.Copies(tile.Prototype.Id) >= current.Protocol.MaxCopies ? new Color(0.6f, 0.6f, 0.6f) : Colors.White; }
        }
        void RefreshPool()
        {
            if (current is null) { return; }
            Ui.Clear(cardGrid);
            foreach (var card in current.Pool.Where(card => MatchesSearch(card, search.Text)))
            {
                var tile = CardTile.CreatePrototype(card); cardGrid.AddChild(tile);
                tile.PrototypeClicked = value => { if (current.Add(value.Id)) { RefreshRows(); } };
            }
            RefreshRows();
        }
        void OpenEditor(string key, DesktopDeckDraft draft)
        {
            try { _catalog.RequireAvailableDeck(draft.Build()); }
            catch (InvalidDataException error) { _status.Text = error.Message; return; }
            draft.UseProtocol(_protocol);
            currentKey = key; current = draft; _deckProfession = draft.Profession;
            manager.Hide(); picker.Hide(); editor.Show();
            editor.GetNode<Label>("ProfessionName").Text = Ui.Profession(draft.Profession);
            editor.GetNode<Label>("Help").Text = $"点击卡牌加入牌组 · {_protocol.ConstructionLabel} · 可前往对战协议调整构筑要求。";
            name.Text = draft.Name; search.Text = ""; RefreshPool();
        }
        var deleteDialog = new ConfirmationDialog { Name = "DeleteDeckDialog", Title = "删除牌组", OkButtonText = "删除", CancelButtonText = "取消" };
        page.AddChild(deleteDialog); Action? pendingDelete = null;
        deleteDialog.Confirmed += () =>
        {
            try { pendingDelete?.Invoke(); BuildMenu(); _menuScreen.GetNode<TabContainer>("Tabs").CurrentTab = 4; _status.Text = "牌组已删除。"; }
            catch (Exception error) { _status.Text = error.Message; }
        };
        void AddDeckTile(string title, string profession, string status, Action open, Action? delete = null)
        {
            var tile = Ui.Instantiate<Button>("DeckTile"); deckGrid.AddChild(tile);
            tile.GetNode<Label>("Name").Text = title; tile.GetNode<Label>("Profession").Text = Ui.Profession(profession);
            tile.GetNode<Label>("Crest").Text = ProfessionCrest(profession); tile.GetNode<Label>("Status").Text = status;
            tile.TooltipText = status;
            tile.Pressed += open;
            var remove = tile.GetNode<Button>("Delete"); remove.Visible = delete is not null;
            remove.Pressed += () => { pendingDelete = delete; deleteDialog.DialogText = $"删除“{title}”及其未保存修改？"; deleteDialog.PopupCentered(); };
        }
        void ShowManager()
        {
            editor.Hide(); picker.Hide(); manager.Show(); Ui.Clear(deckGrid);
            foreach (var deck in _decks)
            {
                var saved = _deckFiles.TryGetValue(deck, out var storedId);
                var key = saved ? "saved:" + storedId : "default:" + deck.Profession;
                var hasDraft = _workshopDrafts.TryGetValue(key, out var draft);
                var missing = _catalog.MissingContent(deck).Union(hasDraft ? _catalog.MissingContent(draft!.Build()) : [], StringComparer.Ordinal).ToArray();
                AddDeckTile(hasDraft ? draft!.Name : deck.Name == deck.Profession ? Ui.Profession(deck.Profession) + "默认牌组" : deck.Name,
                    deck.Profession, missing.Length > 0 ? $"缺少 {missing.Length} 项内容 · 已保留，暂不可用"
                        : hasDraft ? $"草稿 · {draft!.Count} / {_protocol.RequiredDeckSize}" : $"{deck.Cards.Sum(card => card.Copies)} 张 · 点击编辑", () =>
                    {
                        if (missing.Length > 0)
                        { _status.Text = "牌组已保留；请先启用包含以下卡牌的内容包，再编辑或对战：\n" + string.Join("、", missing); return; }
                        if (!_workshopDrafts.TryGetValue(key, out var editing))
                        {
                            editing = new DesktopDeckDraft(_catalog, deck.Profession, _protocol); editing.Load(deck);
                            if (deck.Name == deck.Profession) { editing.Name = Ui.Profession(deck.Profession) + "牌组"; }
                            _workshopDrafts.Add(key, editing);
                        }
                        OpenEditor(key, editing);
                    }, saved ? () => { _deckStore.Delete(storedId!); _deckFiles.Remove(deck); _decks.Remove(deck); _workshopDrafts.Remove(key); } : null);
            }
            foreach (var (key, draft) in _workshopDrafts.Where(pair => pair.Key.StartsWith("new:", StringComparison.Ordinal)))
            {
                var missing = _catalog.MissingContent(draft.Build());
                AddDeckTile(draft.Name, draft.Profession, missing.IsEmpty ? $"草稿 · {draft.Count} / {_protocol.RequiredDeckSize}"
                    : $"缺少 {missing.Length} 项内容 · 草稿已保留", () => OpenEditor(key, draft), () => _workshopDrafts.Remove(key));
            }
        }

        name.TextChanged += value => { if (current is not null) { current.Name = value; } };
        search.TextChanged += _ => RefreshPool();
        editor.GetNode<Button>("Back").Pressed += ShowManager;
        editor.GetNode<Button>("Default").Pressed += () =>
        {
            if (current is null) { return; }
            var originalName = current.Name; current.Load(_catalog.DefaultDeck(current.Profession, _protocol)); current.Name = originalName; RefreshPool();
        };
        editor.GetNode<Button>("Clear").Pressed += () => { current?.Clear(); RefreshRows(); };
        editor.GetNode<Button>("Save").Pressed += () =>
        {
            try
            {
                if (current is null || currentKey is null) { return; }
                var deck = current.Build();
                if (string.IsNullOrWhiteSpace(deck.Name)) { throw new InvalidDataException("请填写牌组名称。"); }
                if (!_catalog.ValidateDeck(deck, _protocol).IsEmpty) { throw new InvalidDataException($"请按当前协议完成 {_protocol.RequiredDeckSize} 张牌组；检查职业及单卡 {_protocol.MinCopies}–{_protocol.MaxCopies} 张限制。"); }
                var replacing = currentKey.StartsWith("saved:", StringComparison.Ordinal) ? currentKey[6..] : null;
                if (_decks.Any(value => value.Name == deck.Name && (!_deckFiles.TryGetValue(value, out var id) || id != replacing)))
                { throw new InvalidDataException("此名称已有牌组，请换一个名称。"); }
                var storedId = _deckStore.Save(deck, replacing);
                foreach (var old in _deckFiles.Where(pair => pair.Value == replacing).Select(pair => pair.Key).ToArray())
                { _deckFiles.Remove(old); _decks.Remove(old); }
                _decks.Add(deck); _deckFiles.Add(deck, storedId); _workshopDrafts.Remove(currentKey);
                BuildMenu(); _menuScreen.GetNode<TabContainer>("Tabs").CurrentTab = 4; _status.Text = "牌组已保存。";
            }
            catch (Exception error) { _status.Text = error.Message; }
        };
        var templatePicker = picker.GetNode<OptionButton>("Panel/Template");
        DesktopDeck[] templates = [];
        void SelectProfession(string profession)
        {
            _deckProfession = profession;
            foreach (var value in Professions) { picker.GetNode<Button>("Panel/ProfessionScroll/Professions/" + value).SetPressedNoSignal(value == profession); }
            picker.GetNode<Label>("Panel/Description").Text = ProfessionDescription(profession);
            templates = _catalog.ArchetypeDecks.Where(deck => deck.Profession == profession && _catalog.ValidateDeck(deck, _protocol).IsEmpty).ToArray();
            templatePicker.Clear(); templatePicker.AddItem("空白牌组");
            foreach (var template in templates) { templatePicker.AddItem(template.Name); }
            templatePicker.Select(0);
            picker.GetNode<Label>("Panel/TemplateHint").Text = templates.Length > 0
                ? "选择体系作为起点，进入编辑后可自由调整。" : "当前协议或内容包下没有可用模板，可以从空白牌组开始。";
        }
        var professionButtons = picker.GetNode<HBoxContainer>("Panel/ProfessionScroll/Professions");
        Ui.Clear(professionButtons);
        foreach (var profession in Professions)
        {
            var button = new Button { Name = profession, Text = ProfessionCrest(profession) + "  " + Ui.Profession(profession),
                ToggleMode = true, CustomMinimumSize = new Vector2(174, 120), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            professionButtons.AddChild(button); button.Pressed += () => SelectProfession(profession);
        }
        if (!Professions.Contains(_deckProfession, StringComparer.Ordinal)) { _deckProfession = Professions.FirstOrDefault() ?? ""; }
        manager.GetNode<Button>("New").Disabled = Professions.Length == 0;
        manager.GetNode<Button>("New").Pressed += () => { SelectProfession(_deckProfession); picker.Show(); };
        picker.GetNode<Button>("Panel/Cancel").Pressed += picker.Hide;
        picker.GetNode<Button>("Panel/Create").Pressed += () =>
        {
            var key = "new:" + Guid.NewGuid().ToString("N");
            var draft = new DesktopDeckDraft(_catalog, _deckProfession, _protocol) { Name = Ui.Profession(_deckProfession) + "新牌组" };
            if (templatePicker.Selected > 0) { draft.Load(templates[templatePicker.Selected - 1]); }
            _workshopDrafts.Add(key, draft); OpenEditor(key, draft);
        };
        ShowManager();
    }

    private static string ProfessionCrest(string profession) => Ui.ProfessionInfo(profession)?.Crest ?? "◇";
    private static string ProfessionDescription(string profession) => Ui.ProfessionInfo(profession)?.Description ?? "";
}
