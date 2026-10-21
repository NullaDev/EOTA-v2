using Eota.Client.Desktop;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private DesktopContentPackages _packageStore = null!;
    private bool _reloadPackages = true;
    private Window? _packageWindow;

    private void ShowContentPackMismatch(string message)
    {
        var window = new Window { Name = "ContentPackMismatch", Title = "内容包与主机不一致", Size = new Vector2I(900, 520), Transient = true };
        AddChild(window); window.CloseRequested += window.QueueFree;
        var margin = new MarginContainer(); margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "top", "right", "bottom" }) { margin.AddThemeConstantOverride("margin_" + side, 24); }
        window.AddChild(margin);
        var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 16); margin.AddChild(body);
        body.AddChild(Ui.Text("请与主机启用相同的内容包", 26));
        body.AddChild(new RichTextLabel { Text = message.Replace("content-pack-mismatch\n", "", StringComparison.Ordinal),
            SelectionEnabled = true, SizeFlagsVertical = SizeFlags.ExpandFill });
        var manage = new Button { Text = "管理本机内容包" }; body.AddChild(manage);
        manage.Pressed += () => { window.QueueFree(); ShowContentPackages(); };
        window.PopupCentered();
        _status.Text = "内容包不一致：请核对主机与本机的版本和 hash。";
    }

    private bool LoadSelectedPackages()
    {
        if (!_reloadPackages) { return true; }
        try
        {
            _packageStore.MigrateLegacySelection(ProjectSettings.GlobalizePath("user://content-selection.txt"));
            _catalog = _packageStore.LoadCatalog(_packageStore.ReadSelection());
            CardTile.UseCatalog(_catalog); Ui.UseCatalog(_catalog);
            foreach (var draft in _workshopDrafts.Values) { draft.UseCatalog(_catalog); }
            _reloadPackages = false;
            _roomClient?.Dispose(); _roomClient = null;
            _decks.RemoveAll(d => !_deckFiles.ContainsKey(d));
            _decks.InsertRange(0, Professions.Select(p => _catalog.DefaultDeck(p, _protocol)));
            return true;
        }
        catch (Exception error)
        {
            _battle = false; Ui.Clear(_root);
            var screen = new Control { SizeFlagsVertical = SizeFlags.ExpandFill }; _root.AddChild(screen);
            AddTableBackdrop(screen, false);
            var center = new CenterContainer(); center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); screen.AddChild(center);
            var panel = new PanelContainer { CustomMinimumSize = new Vector2(1000, 480) }; center.AddChild(panel);
            var margin = new MarginContainer(); foreach (var side in new[] { "left", "top", "right", "bottom" }) { margin.AddThemeConstantOverride("margin_" + side, 32); }
            panel.AddChild(margin);
            var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 18); margin.AddChild(body);
            body.AddChild(Ui.Text("内容包尚未就绪", 32));
            body.AddChild(Ui.Text("解决以下问题后才能开始游戏。保存的选择和牌组均已保留。", 20));
            var details = new RichTextLabel { Text = error.Message, SelectionEnabled = true, CustomMinimumSize = new Vector2(900, 230), SizeFlagsVertical = SizeFlags.ExpandFill };
            body.AddChild(details);
            var repair = new Button { Text = "管理内容包", CustomMinimumSize = new Vector2(240, 56) };
            repair.Pressed += ShowContentPackages; body.AddChild(repair);
            _status = Ui.Text(""); body.AddChild(_status);
            return false;
        }
    }

    private void ShowContentPackages()
    {
        if (GodotObject.IsInstanceValid(_packageWindow)) { _packageWindow!.QueueFree(); }
        var window = new Window { Title = "内容包", Size = new Vector2I(1000, 720), MinSize = new Vector2I(800, 600), Transient = true };
        _packageWindow = window; AddChild(window); window.CloseRequested += window.QueueFree;
        var margin = new MarginContainer { OffsetLeft = 24, OffsetTop = 20, OffsetRight = -24, OffsetBottom = -20 };
        margin.SetAnchorsPreset(LayoutPreset.FullRect); window.AddChild(margin);
        var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 12); margin.AddChild(body);
        body.AddChild(Ui.Text("选择本次旅程的内容", 28));
        body.AddChild(Ui.Text(_session is null ? "保存后加载所选包。缺少依赖或内容冲突会阻止开局。" :
            "保存后返回大厅生效。当前对局及已开放的服务器使用开局时的内容。", 18));
        var feedback = new RichTextLabel { Name = "Feedback", SelectionEnabled = true, CustomMinimumSize = new Vector2(0, 110),
            Text = "勾选需要的内容包后保存，也可以先检查依赖与冲突。" };
        var selection = new HashSet<string>(StringComparer.Ordinal);
        try { selection.UnionWith(_packageStore.ReadSelection()); }
        catch (Exception error) { feedback.Text = error.Message + "\n重新选择并保存可以修复此设置。"; }
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; body.AddChild(scroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(rows);
        var installed = _packageStore.Scan();
        foreach (var pack in installed)
        {
            var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 5); rows.AddChild(box);
            var row = new HBoxContainer(); box.AddChild(row);
            var check = new CheckBox { Name = "Pack_" + pack.Id, Text = $"{pack.Name}  ·  {pack.Id}  ·  {pack.Version}", ButtonPressed = selection.Contains(pack.Id),
                SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = pack.Path };
            row.AddChild(check);
            check.Toggled += on => { if (on) { selection.Add(pack.Id); } else { selection.Remove(pack.Id); } };
            if (pack.Error is null && _catalog is not null && !_reloadPackages && _session is null)
            {
                var edit = new Button { Text = pack.BuiltIn ? "编辑副本" : "编辑" }; row.AddChild(edit);
                edit.Pressed += () =>
                {
                    try
                    {
                        _contentEditor = DesktopContentEditor.OpenPackage(_catalog, pack.Path);
                        if (pack.BuiltIn) { _contentEditor.PackageId = "custom-" + pack.Id; _contentEditor.PackageName += " · 副本"; }
                        _editorSnapshot = null; _editingCard = null!; _setupTab = 8; window.QueueFree(); BuildMenu();
                    }
                    catch (Exception error) { feedback.Text = error.Message; }
                };
            }
            box.AddChild(Ui.Text($"{pack.CardCount} 张卡牌  ·  {(pack.BuiltIn ? "内置" : "已安装")}  ·  依赖：{string.Join("，", pack.Dependencies.DefaultIfEmpty("无"))}", 16));
            var hash = new LineEdit { Text = "SHA-256  " + pack.Hash, Editable = false, TooltipText = pack.Hash };
            box.AddChild(hash);
            if (pack.Error is not null) { box.AddChild(Ui.Text(pack.Error, 16)); }
            if (!pack.BuiltIn)
            {
                var remove = new Button { Text = "卸载" }; row.AddChild(remove);
                remove.Pressed += () =>
                {
                    var confirm = new ConfirmationDialog { Title = "卸载内容包", DialogText = $"删除已安装的 {pack.Id}？依赖它的包将无法加载。", OkButtonText = "卸载", CancelButtonText = "取消" };
                    window.AddChild(confirm);
                    confirm.Confirmed += () =>
                    {
                        try { _packageStore.Remove(pack); _reloadPackages = true; ShowContentPackages(); }
                        catch (Exception error) { feedback.Text = error.Message; }
                    };
                    confirm.PopupCentered();
                };
            }
            box.AddChild(new HSeparator());
        }
        foreach (var missing in selection.Except(installed.Select(p => p.Id), StringComparer.Ordinal).ToArray())
        {
            var check = new CheckBox { Text = missing + " · 未找到安装文件", ButtonPressed = true };
            rows.AddChild(check);
            check.Toggled += on => { if (on) { selection.Add(missing); } else { selection.Remove(missing); } };
        }
        body.AddChild(feedback);
        var actions = new HBoxContainer(); body.AddChild(actions);
        var import = new Button { Text = "安装内容包…" }; actions.AddChild(import);
        var picker = new FileDialog { Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.OpenFile,
            Filters = ["*.json ; EOTA 内容包"], UseNativeDialog = true };
        window.AddChild(picker); import.Pressed += () => picker.PopupCentered(new Vector2I(900, 600));
        picker.FileSelected += path =>
        {
            try
            {
                var id = DesktopContentEditor.ReadPackage(path).Id;
                if (installed.Any(p => !p.BuiltIn && p.Id == id))
                {
                    var confirm = new ConfirmationDialog { Title = "替换已安装的内容包", DialogText = $"将替换 {id} 的安装文件。当前对局继续使用已加载的内容。", OkButtonText = "替换", CancelButtonText = "取消" };
                    window.AddChild(confirm);
                    confirm.Confirmed += () => Install(path, true); confirm.PopupCentered();
                }
                else { Install(path, false); }
            }
            catch (Exception error) { feedback.Text = error.Message; }
        };
        void Install(string path, bool replace)
        {
            try { _packageStore.Install(path, replace); ShowContentPackages(); }
            catch (Exception error) { feedback.Text = error.Message; }
        }
        var validate = new Button { Text = "检查所选内容" }; actions.AddChild(validate);
        validate.Pressed += () =>
        {
            try { var catalog = _packageStore.LoadCatalog(selection); feedback.Text = $"校验通过：{catalog.Packages.Length} 个内容包，{catalog.Cards.Length} 张卡牌。"; }
            catch (Exception error) { feedback.Text = error.Message; }
        };
        var save = new Button { Name = "SaveSelection", Text = _session is null ? "保存并加载" : "保存，下次返回大厅生效", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        actions.AddChild(save);
        save.Pressed += () =>
        {
            try
            {
                _packageStore.SaveSelection(selection); _reloadPackages = true; window.QueueFree();
                if (_session is null) { BuildMenu(); }
                else { _status.Text = "内容包选择已保存，返回大厅后加载。"; }
            }
            catch (Exception error) { feedback.Text = error.Message; }
        };
        window.PopupCentered();
    }
}
