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
            foreach (var entry in current.Build().Cards)
            {
                var card = _catalog.Cards.FirstOrDefault(card => card.Id == entry.Id);
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
                AddDeckTile(hasDraft ? draft!.Name : deck.Name == deck.Profession ? Ui.Profession(deck.Profession) + "默认牌组" : deck.Name,
                    deck.Profession, hasDraft ? $"草稿 · {draft!.Count} / {_protocol.RequiredDeckSize}" : $"{deck.Cards.Sum(card => card.Copies)} 张 · 点击编辑", () =>
                    {
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
            { AddDeckTile(draft.Name, draft.Profession, $"草稿 · {draft.Count} / {_protocol.RequiredDeckSize}", () => OpenEditor(key, draft), () => _workshopDrafts.Remove(key)); }
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
            foreach (var value in Professions) { picker.GetNode<Button>("Panel/Professions/" + value).SetPressedNoSignal(value == profession); }
            picker.GetNode<Label>("Panel/Description").Text = ProfessionDescription(profession);
            templates = _catalog.ArchetypeDecks.Where(deck => deck.Profession == profession && _catalog.ValidateDeck(deck, _protocol).IsEmpty).ToArray();
            templatePicker.Clear(); templatePicker.AddItem("空白牌组");
            foreach (var template in templates) { templatePicker.AddItem(template.Name); }
            templatePicker.Select(0);
            picker.GetNode<Label>("Panel/TemplateHint").Text = templates.Length > 0
                ? "选择体系作为起点，进入编辑后可自由调整。" : "当前协议或内容包下没有可用模板，可以从空白牌组开始。";
        }
        foreach (var profession in Professions)
        { picker.GetNode<Button>("Panel/Professions/" + profession).Pressed += () => SelectProfession(profession); }
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

    private static string ProfessionCrest(string profession) => profession switch
    { "Guardian" => "♜", "Arcanist" => "✦", "Artisan" => "⚒", "Hunter" => "➶", _ => "◇" };

    // Preserve the original Chinese profession lore; its source is documented in the P9.6 review.
    // Prototype's Artificer presentation key maps to the current Artisan profession.
    private static string ProfessionDescription(string profession) => profession switch
    {
        "Guardian" => "守卫并非天生的英雄，他们只是城墙下的普通人，是在兽潮、饥荒与贵族命令之间一次次活下来的士兵。魔法师的时代正在衰落，而剑盾、长矛与纪律正在重新定义城邦的未来。守卫擅长动员大量士兵，并通过号令、装备与阵线增幅将平凡之人凝聚成不可撼动的防线。选择守卫，意味着你将以稳固的布阵、持续的增援和集体强化压垮对手，让敌人明白：一个人或许微不足道，但一整座城邦不会退后半步。",
        "Arcanist" => "曾经统御世界的魔法师协会，如今正站在权威崩塌的边缘。以太枯竭让他们失去了往日呼风唤雨的力量，却未能熄灭他们继续执掌世界的野心。奥术师钻研上古遗物、铺设复杂法阵，在短暂活化的以太中重现旧时代的辉煌。选择奥术师，意味着你将掌控绚烂而短促的爆发时机，用精密的法术与瞬间增强改写战局；但若错过最佳窗口，昔日荣光也会像残焰一样迅速熄灭。",
        "Hunter" => "当大多数人退入城墙之后，猎人仍然行走在荒野之中。他们不相信秩序，也不迷信荣耀，只相信陷阱、毒箭、野兽伙伴和活下去的本能。猎人公会的战术极其灵活，能够利用远程打击、伏击、剧毒与野兽协同不断削弱猎物，避免无谓的正面对抗。选择猎人，意味着你将用耐心和狡黠掌控战场节奏，在敌人意识到自己已经落入圈套之前，便一点点剥夺他们所有反击的可能。",
        "Artisan" => "工匠是被埋葬文明的继承者，也是新时代最危险的开拓者。他们从废墟中挖掘蒸汽机械的残骸，用考古的方式复兴科学，用齿轮、锅炉和钢铁巨兽挑战魔法师残存的统治。工匠能够快速积累资源，提前部署远超时代的庞大机械；这些造物笨重、迟缓，甚至需要额外代价才能完全启动，但一旦运转起来，便足以碾碎整条战线。选择工匠，意味着你愿意用前期的准备换取后期压倒性的机械力量，让沉睡的钢铁巨兽在战场上苏醒。",
        _ => ""
    };
}
