using System.Text.Json;
using System.Text.Json.Nodes;
using Eota.Client.Desktop;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private DesktopContentEditor? _contentEditor;
    private Control _editorPage = null!;
    private string? _editingId;
    private JsonObject _editingCard = null!;
    private bool _editorLoading;
    private int _editorTab;
    private bool _editorDirty;
    private string? _pendingEditorSelection;
    private Godot.Timer? _editorPreviewTimer;
    private EditorDraftSnapshot? _editorSnapshot;
    private sealed record EditorDraftSnapshot(string BaseJson, string? Id, bool Dirty, int Tab, Dictionary<string, string> Values, string Effects, string Raw);

    private void CaptureEditorDraft()
    {
        if (!GodotObject.IsInstanceValid(_editorPage) || !_editorPage.IsInsideTree() || _editingCard is null) { return; }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var child in _editorPage.GetNode<GridContainer>("Pages/Basic/Margin/Fields").GetChildren())
        {
            var text = child switch { LineEdit line => line.Text, TextEdit edit => edit.Text,
                OptionButton option => option.Selected.ToString(), CheckBox check => check.ButtonPressed.ToString(), _ => null };
            if (text is not null) { values[child.Name] = text; }
        }
        _editorSnapshot = new(_editingCard.ToJsonString(EditorJson), _editingId, _editorDirty, _editorTab, values,
            _editorPage.GetNode<TextEdit>("Pages/Effects").Text, _editorPage.GetNode<TextEdit>("Pages/Raw").Text);
    }
    private static readonly JsonSerializerOptions EditorJson = new() { WriteIndented = true };
    private string EditorDraftPath => ProjectSettings.GlobalizePath("user://content/draft.eotapack.json");
    private T EditorField<T>(string name) where T : Node => _editorPage.GetNode<T>("Pages/Basic/Margin/Fields/" + name);

    private void BuildContentEditor(TabContainer tabs)
    {
        _editorPage = tabs.GetNode<Control>("Editor");
        _editorPreviewTimer = new Godot.Timer { WaitTime = 0.4, OneShot = true };
        _editorPage.AddChild(_editorPreviewTimer); _editorPreviewTimer.Timeout += RefreshEditorPreview;
        if (_contentEditor is null)
        {
            try { _contentEditor = new DesktopContentEditor(File.Exists(EditorDraftPath) ? new DesktopCatalog(_catalog.Root, EditorDraftPath) : _catalog); }
            catch (Exception error) { _contentEditor = new DesktopContentEditor(_catalog); GD.PushWarning("编辑草稿未加载：" + error.Message); }
        }
        var pages = _editorPage.GetNode<TabContainer>("Pages"); pages.SetTabTitle(0, "基本属性"); pages.SetTabTitle(1, "效果 JSON"); pages.SetTabTitle(2, "完整 JSON");
        void Options(string field, string[] titles)
        { var option = EditorField<OptionButton>(field); foreach (var title in titles) { option.AddItem(title); } }
        Options("Kind", ["随从", "法术", "场地"]); Options("Source", ["基础卡", "衍生卡"]);
        Options("Profession", ["中立", "守卫", "奥术师", "工匠", "猎人", "灵魂使"]); Options("Speed", ["快速", "慢速"]); Options("Scope", ["一路", "全局"]);
        var profession = _editorPage.GetNode<OptionButton>("FilterProfession");
        foreach (var value in new[] { "全部职业", "中立", "守卫", "奥术师", "工匠", "猎人", "灵魂使" }) { profession.AddItem(value); }
        var kind = _editorPage.GetNode<OptionButton>("FilterKind");
        foreach (var value in new[] { "全部类型", "随从", "法术", "场地" }) { kind.AddItem(value); }
        var search = _editorPage.GetNode<LineEdit>("Search"); search.TextChanged += _ => RefreshEditorList();
        profession.ItemSelected += _ => RefreshEditorList(); kind.ItemSelected += _ => RefreshEditorList();
        var list = _editorPage.GetNode<ItemList>("List");
        var discard = new ConfirmationDialog { Title = "未保存的修改", DialogText = "当前卡牌尚未保存。放弃这些修改并切换？", OkButtonText = "放弃修改", CancelButtonText = "继续编辑" };
        _editorPage.AddChild(discard);
        discard.Confirmed += () => { if (_pendingEditorSelection is { } id) { LoadEditorCard(_contentEditor.Read(id), id); } };
        discard.Canceled += RefreshEditorList;
        list.ItemSelected += index =>
        {
            var id = list.GetItemMetadata((int)index).AsString();
            if (_editorDirty && id != _editingId) { _pendingEditorSelection = id; discard.PopupCentered(); }
            else if (id != _editingId) { LoadEditorCard(_contentEditor.Read(id), id); }
        };
        _editorPage.GetNode<Button>("New").Pressed += () =>
        {
            if (_editorDirty) { _status.Text = "请先保存当前卡牌，再新建。"; return; }
            LoadEditorCard(DesktopContentEditor.NewCard(), null); _editorDirty = true;
        };
        _editorPage.GetNode<Button>("Duplicate").Pressed += () => EditorAction(() =>
        {
            var value = ReadEditorCard(); var card = JsonNode.Parse(value.Json)!.AsObject();
            card["id"] = JsonNode.Parse(DesktopContentEditor.NewCard().Json)!["id"]!.GetValue<string>();
            LoadEditorCard(new EditableCard(card.ToJsonString(EditorJson), value.Name + " 副本", value.Description), null); _editorDirty = true;
        });
        var delete = new ConfirmationDialog { Title = "删除卡牌", DialogText = "从编辑中的内容包移除此卡牌？内置内容不受影响。", OkButtonText = "删除", CancelButtonText = "取消" };
        _editorPage.AddChild(delete);
        _editorPage.GetNode<Button>("Delete").Pressed += () => { if (_editingId is not null) { delete.PopupCentered(); } };
        delete.Confirmed += () => EditorAction(() => { _contentEditor.DeleteCard(_editingId!); _contentEditor.SavePack(EditorDraftPath); LoadEditorCard(DesktopContentEditor.NewCard(), null); RefreshEditorList(); });
        _editorPage.GetNode<Button>("Restore").Pressed += () => EditorAction(() =>
        {
            if (_editingId is null) { return; }
            var original = new DesktopContentEditor(new DesktopCatalog(_catalog.Root));
            if (!original.CardIds.Contains(_editingId)) { throw new InvalidDataException("这是一张自定义卡，没有内置版本。"); }
            LoadEditorCard(original.Read(_editingId), _editingId); _editorDirty = true; RefreshEditorPreview();
        });
        _editorPage.GetNode<Button>("Validate").Pressed += RefreshEditorPreview;
        _editorPage.GetNode<Button>("Save").Pressed += () => EditorAction(() =>
        {
            var value = ReadEditorCard(); _editingId = _contentEditor.SaveCard(value, _editingId); _contentEditor.SavePack(EditorDraftPath);
            _editorDirty = false; RefreshEditorList(); RefreshEditorPreview(); _status.Text = "卡牌已保存到编辑内容包。";
        });
        foreach (var node in _editorPage.GetNode<GridContainer>("Pages/Basic/Margin/Fields").GetChildren())
        {
            if (node is LineEdit line) { line.TextChanged += _ => MarkEditorDirty(); }
            if (node is TextEdit text) { text.TextChanged += MarkEditorDirty; }
            if (node is OptionButton option) { option.ItemSelected += _ => MarkEditorDirty(); }
            if (node is CheckBox check) { check.Toggled += _ => MarkEditorDirty(); }
        }
        EditorField<OptionButton>("Kind").ItemSelected += _ =>
        {
            if (_editorLoading || _editingCard is null) { return; }
            var value = ReadEditorCard(); var card = JsonNode.Parse(value.Json)!.AsObject();
            DesktopContentEditor.ChangeKind(card, new[] { "minion", "spell", "field" }[EditorField<OptionButton>("Kind").Selected]);
            LoadEditorCard(value with { Json = card.ToJsonString(EditorJson) }, _editingId); _editorDirty = true;
        };
        EditorField<CheckBox>("Permanent").Toggled += _ => RefreshEditorLifetime();
        _editorPage.GetNode<TextEdit>("Pages/Effects").TextChanged += MarkEditorDirty;
        _editorPage.GetNode<TextEdit>("Pages/Raw").TextChanged += MarkEditorDirty;
        pages.TabChanged += index => EditorAction(() =>
        {
            if (_editorLoading || _editingCard is null) { return; }
            var value = ReadEditorCard(); var dirty = _editorDirty;
            if (_editorTab == 2)
            {
                var errors = _contentEditor.Validate(value, _editingId);
                if (!errors.IsEmpty)
                {
                    _editorLoading = true; pages.CurrentTab = 2; _editorLoading = false;
                    throw new InvalidDataException(string.Join('\n', errors));
                }
                LoadEditorCard(value, _editingId);
            }
            else { _editorLoading = true; _editorPage.GetNode<TextEdit>("Pages/Raw").Text = value.Json; _editorLoading = false; }
            _editorTab = (int)index; _editorDirty = dirty;
        });
        var packDialog = _editorPage.GetNode<Window>("PackDialog"); packDialog.CloseRequested += packDialog.Hide;
        _editorPage.GetNode<Button>("Pack").Pressed += () =>
        {
            packDialog.GetNode<Label>("PackStatus").Text = $"当前启用：{(_catalog.ContentPackPath is null ? "内置内容包" : "自定义内容包")}\n规则指纹：{_catalog.RuleHash[..20]}…";
            packDialog.PopupCentered();
        };
        var picker = new FileDialog { Access = FileDialog.AccessEnum.Filesystem, Filters = ["*.json ; EOTA V2 内容包"], UseNativeDialog = true };
        _editorPage.AddChild(picker); var importing = false; var exportSingle = false;
        packDialog.GetNode<Button>("Import").Pressed += () => { importing = true; picker.FileMode = FileDialog.FileModeEnum.OpenFile; picker.PopupCentered(new Vector2I(900, 640)); };
        void BeginExport(bool single) => EditorAction(() =>
        {
            if (_editorDirty || single && _editingId is null) { throw new InvalidDataException("请先保存当前卡牌，再导出。"); }
            importing = false; exportSingle = single; picker.FileMode = FileDialog.FileModeEnum.SaveFile;
            picker.CurrentFile = single ? _editingId + ".eotapack.json" : "custom.eotapack.json";
            picker.PopupCentered(new Vector2I(900, 640));
        });
        packDialog.GetNode<Button>("Export").Pressed += () => BeginExport(false);
        _editorPage.GetNode<Button>("ExportPack").Pressed += () => BeginExport(false);
        _editorPage.GetNode<Button>("ExportCard").Pressed += () => BeginExport(true);
        picker.FileSelected += path => EditorAction(() =>
        {
            if (_editorDirty) { throw new InvalidDataException("请先保存当前卡牌。"); }
            if (importing)
            {
                _contentEditor.ImportCards(path); _contentEditor.SavePack(EditorDraftPath);
                var first = DesktopContentEditor.LoadPack(path).Cards[0].GetProperty("id").GetString()!;
                LoadEditorCard(_contentEditor.Read(first), first); RefreshEditorList();
            }
            else if (exportSingle) { _contentEditor.SaveCards(path, [_editingId!]); }
            else { _contentEditor.SavePack(path); }
            _status.Text = importing ? "卡牌已合并到编辑内容包，可编辑或启用。" : exportSingle ? "卡牌及相关衍生卡已导出。" : "内容包已导出。";
        });
        packDialog.GetNode<Button>("Activate").Pressed += () => EditorAction(() =>
        {
            if (_editorDirty) { throw new InvalidDataException("请先保存当前卡牌，再启用内容包。"); }
            var path = ProjectSettings.GlobalizePath("user://content/active.eotapack.json"); _contentEditor.SavePack(path);
            ActivateContentPack(path);
        });
        packDialog.GetNode<Button>("Builtin").Pressed += () => EditorAction(() => ActivateContentPack(null));
        RefreshEditorList();
        if (_editorSnapshot is { } snapshot)
        {
            LoadEditorCard(new EditableCard(snapshot.BaseJson, snapshot.Values["Name"], snapshot.Values["Description"]), snapshot.Id);
            _editorLoading = true;
            foreach (var pair in snapshot.Values)
            {
                switch (EditorField<Control>(pair.Key))
                {
                    case LineEdit line: line.Text = pair.Value; break;
                    case TextEdit edit: edit.Text = pair.Value; break;
                    case OptionButton option: option.Select(int.Parse(pair.Value)); break;
                    case CheckBox check: check.ButtonPressed = bool.Parse(pair.Value); break;
                }
            }
            _editorPage.GetNode<TextEdit>("Pages/Effects").Text = snapshot.Effects; _editorPage.GetNode<TextEdit>("Pages/Raw").Text = snapshot.Raw;
            pages.CurrentTab = snapshot.Tab; _editorTab = snapshot.Tab; _editorDirty = snapshot.Dirty; _editorLoading = false;
            RefreshEditorPreview();
        }
        else { var first = _contentEditor.CardIds.First(); LoadEditorCard(_contentEditor.Read(first), first); }
    }

    private void ActivateContentPack(string? path)
    {
        _catalog = new DesktopCatalog(_catalog.Root, path);
        for (var index = 0; index < _decks.Count; index++)
        { if (_decks[index].Name == _decks[index].Profession) { _decks[index] = _catalog.DefaultDeck(_decks[index].Profession); } }
        File.WriteAllText(ProjectSettings.GlobalizePath("user://content-selection.txt"), path ?? "");
        _setupTab = 8; BuildMenu(); _status.Text = path is null ? "已启用内置内容包。" : "自定义内容包已启用；现有服务器仍使用开服时的内容。";
    }

    private void MarkEditorDirty()
    {
        if (_editorLoading) { return; }
        _editorDirty = true; _editorPage.GetNode<Label>("Validation").Text = "有未保存的修改";
        _editorPreviewTimer?.Start();
    }
    private void RefreshEditorLifetime()
    {
        var permanent = EditorField<CheckBox>("Permanent").ButtonPressed;
        var energy = EditorField<LineEdit>("Energy"); energy.Editable = !permanent;
        energy.Modulate = permanent ? new Color(1, 1, 1, 0.45f) : Colors.White;
        energy.TooltipText = permanent ? "永久场地没有耐久值。" : "每回合消耗耐久，降至 0 时离场。";
    }
    private void EditorAction(Action action)
    { try { action(); } catch (Exception error) { _editorPage.GetNode<Label>("ValidationScroll/Errors").Text = error.Message; _status.Text = error.Message.Split('\n')[0]; } }

    private void RefreshEditorList()
    {
        var list = _editorPage.GetNode<ItemList>("List"); list.Clear(); var search = _editorPage.GetNode<LineEdit>("Search").Text.Trim();
        var profession = new[] { "", "neutral", "guardian", "arcanist", "artisan", "hunter", "soulweaver" }[_editorPage.GetNode<OptionButton>("FilterProfession").Selected];
        var kind = new[] { "", "minion", "spell", "field" }[_editorPage.GetNode<OptionButton>("FilterKind").Selected];
        foreach (var id in _contentEditor!.CardIds)
        {
            var value = _contentEditor.Read(id); using var json = JsonDocument.Parse(value.Json);
            if ((search.Length > 0 && !id.Contains(search, StringComparison.OrdinalIgnoreCase) && !value.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                || (profession.Length > 0 && json.RootElement.GetProperty("profession").GetString() != profession)
                || (kind.Length > 0 && json.RootElement.GetProperty("kind").GetString() != kind)) { continue; }
            var index = list.AddItem(value.Name); list.SetItemMetadata(index, id); list.SetItemTooltip(index, id);
            if (id == _editingId) { list.Select(index); }
        }
    }

    private void LoadEditorCard(EditableCard value, string? id)
    {
        _editorPreviewTimer?.Stop();
        _editorLoading = true;
        try
        {
            _editingId = id; _editingCard = JsonNode.Parse(value.Json)!.AsObject(); _editorDirty = false;
            string Text(string key, string fallback = "") => _editingCard[key]?.GetValueKind() == JsonValueKind.String ? _editingCard[key]!.GetValue<string>() : _editingCard[key]?.ToJsonString() ?? fallback;
            void Line(string name, string text) => EditorField<LineEdit>(name).Text = text;
            Line("Id", Text("id")); Line("Name", value.Name); Line("Cost", Text("cost", "1")); Line("Attack", Text("attack", "1")); Line("Health", Text("health", "1"));
            Line("Energy", _editingCard["lifetime"]?["energy"]?.ToJsonString() ?? "3"); Line("Texture", Text("texturePath"));
            Line("Tags", string.Join(", ", (_editingCard["tags"] as JsonArray ?? []).Select(v => v!.GetValue<string>())));
            EditorField<TextEdit>("Description").Text = value.Description;
            EditorField<OptionButton>("Kind").Select(Array.IndexOf(new[] { "minion", "spell", "field" }, Text("kind")));
            EditorField<OptionButton>("Source").Select(Text("source") == "token" ? 1 : 0);
            EditorField<OptionButton>("Profession").Select(Array.IndexOf(new[] { "neutral", "guardian", "arcanist", "artisan", "hunter", "soulweaver" }, Text("profession")));
            EditorField<OptionButton>("Speed").Select(Text("speed") == "slow" ? 1 : 0); EditorField<OptionButton>("Scope").Select(Text("targetScope") == "global" ? 1 : 0);
            EditorField<CheckBox>("Permanent").ButtonPressed = _editingCard["lifetime"]?["kind"]?.GetValue<string>() == "permanent";
            _editorPage.GetNode<TextEdit>("Pages/Effects").Text = DesktopContentEditor.AbilitiesJson(_editingCard);
            _editorPage.GetNode<TextEdit>("Pages/Raw").Text = value.Json;
            foreach (var name in new[] { "Attack", "Health", "Permanent", "Energy", "Speed", "Scope" })
            {
                var visible = name is "Attack" or "Health" ? Text("kind") == "minion" : name is "Speed" or "Scope" ? Text("kind") == "spell"
                    : Text("kind") == "field";
                EditorField<Control>(name).Visible = visible; EditorField<Control>(name + "Label").Visible = visible;
            }
            _editorTab = _editorPage.GetNode<TabContainer>("Pages").CurrentTab;
        }
        finally { _editorLoading = false; }
        RefreshEditorPreview();
    }

    private EditableCard ReadEditorCard()
    {
        var name = EditorField<LineEdit>("Name").Text; var description = EditorField<TextEdit>("Description").Text;
        if (_editorTab == 2) { return new(_editorPage.GetNode<TextEdit>("Pages/Raw").Text, name, description); }
        var card = _editingCard.DeepClone().AsObject();
        string Line(string field) => EditorField<LineEdit>(field).Text.Trim();
        long Number(string field) => long.TryParse(Line(field), out var value) ? value : throw new InvalidDataException($"{field} 必须是整数。");
        card["id"] = Line("Id"); card["cost"] = Number("Cost"); card["texturePath"] = Line("Texture");
        card["source"] = EditorField<OptionButton>("Source").Selected == 1 ? "token" : "core";
        card["profession"] = new[] { "neutral", "guardian", "arcanist", "artisan", "hunter", "soulweaver" }[EditorField<OptionButton>("Profession").Selected];
        card["tags"] = new JsonArray(Line("Tags").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        DesktopContentEditor.ApplyAbilities(card, _editorPage.GetNode<TextEdit>("Pages/Effects").Text);
        var kind = card["kind"]!.GetValue<string>();
        if (kind == "minion") { card["attack"] = Number("Attack"); card["health"] = Number("Health"); }
        if (kind == "field")
        {
            card.Remove("power");
            card["lifetime"] = EditorField<CheckBox>("Permanent").ButtonPressed ? new JsonObject { ["kind"] = "permanent" } : new JsonObject { ["kind"] = "finite", ["energy"] = Number("Energy") };
        }
        if (kind == "spell") { card["speed"] = EditorField<OptionButton>("Speed").Selected == 1 ? "slow" : "fast"; card["targetScope"] = EditorField<OptionButton>("Scope").Selected == 1 ? "global" : "lane"; }
        return new(card.ToJsonString(EditorJson), name, description);
    }

    private void RefreshEditorPreview() => EditorAction(() =>
    {
        RefreshEditorLifetime();
        var preview = _editorPage.GetNode<VBoxContainer>("Preview"); Ui.Clear(preview);
        var presentation = _contentEditor!.Preview(ReadEditorCard(), _editingId);
        if (!ResourceLoader.Exists("res://" + presentation.TexturePath) && !File.Exists(ProjectSettings.GlobalizePath("res://" + presentation.TexturePath)))
        { throw new InvalidDataException("插图路径不存在；请选择现有的 Content/Generated/Art/*.png。"); }
        preview.AddChild(CardTile.CreatePrototype(presentation, detail: true));
        _editorPage.GetNode<Label>("Validation").Text = _editorDirty ? "规则校验通过\n有未保存的修改" : "规则校验通过";
        _editorPage.GetNode<Label>("ValidationScroll/Errors").Text = "";
        _status.Text = "";
    });
}
