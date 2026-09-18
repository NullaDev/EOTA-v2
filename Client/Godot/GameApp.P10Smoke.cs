using System.Text.Json.Nodes;
using Eota.Client.Desktop;
using Eota.Transport.Contracts;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private async Task ContentEditorSmokeAsync()
    {
        var savedDraft = File.Exists(EditorDraftPath) ? File.ReadAllBytes(EditorDraftPath) : null;
        try
        {
            _contentEditor = new DesktopContentEditor(_catalog); _editingCard = null!; BuildMenu();
            _menuScreen.GetNode<TabContainer>("Tabs").CurrentTab = 8;
            await CaptureInteraction("p10-editor-minion");
            Require(_editorPage.GetNode<VBoxContainer>("Preview").GetChildCount() == 1, "Editor previews a prototype");
            const string id = "EOTA-CORE-GUA-MIN-001"; LoadEditorCard(_contentEditor.Read(id), id);
            EditorField<LineEdit>("Attack").Text = "999999"; EditorField<LineEdit>("Attack").EmitSignal(LineEdit.SignalName.TextChanged, "999999");
            _editorPage.GetNode<Button>("Validate").EmitSignal(BaseButton.SignalName.Pressed);
            Require(_editorPage.GetNode<VBoxContainer>("Preview").GetChild<CardTile>(0).Prototype.Attack == 999999, "Edited stat preview");
            _editorPage.GetNode<Button>("Save").EmitSignal(BaseButton.SignalName.Pressed);
            Require(File.Exists(EditorDraftPath) && new DesktopCatalog(_catalog.Root, EditorDraftPath).RuleHash != _catalog.RuleHash, "Draft saves changed hash separately");
            Require(new DesktopCatalog(_catalog.Root).RuleHash == _catalog.RuleHash, "Built-in rules remain intact");
            EditorField<LineEdit>("Attack").Text = "27"; EditorField<LineEdit>("Attack").EmitSignal(LineEdit.SignalName.TextChanged, "27");
            BuildMenu(); _menuScreen.GetNode<TabContainer>("Tabs").CurrentTab = 8;
            Require(EditorField<LineEdit>("Attack").Text == "27" && _editorDirty, "Menu rebuilding retains unsaved editor fields");
            LoadEditorCard(_contentEditor.Read("EOTA-CORE-GUA-SPL-002"), "EOTA-CORE-GUA-SPL-002");
            _editorPage.GetNode<TabContainer>("Pages").CurrentTab = 1; await CaptureInteraction("p10-editor-effects");
            var effects = _editorPage.GetNode<TextEdit>("Pages/Effects"); var original = effects.Text;
            effects.Text = "[{ invalid ]"; _editorPage.GetNode<Button>("Validate").EmitSignal(BaseButton.SignalName.Pressed);
            Require(_editorPage.GetNode<Label>("ValidationScroll/Errors").Text.Length > 0 && _editorPage.GetNode<VBoxContainer>("Preview").GetChildCount() == 0, "Invalid JSON clears preview and reports error");
            effects.Text = original;
            _editorPage.GetNode<TabContainer>("Pages").CurrentTab = 0;
            LoadEditorCard(_contentEditor.Read("EOTA-CORE-GUA-FLD-001"), "EOTA-CORE-GUA-FLD-001");
            Require(EditorField<LineEdit>("Energy").Visible && !EditorField<LineEdit>("Attack").Visible, "Type-specific fields");
            Require(_editorPage.GetNodeOrNull<Control>("Pages/Basic/Margin/Fields/Power") is null
                && _editorPage.GetNodeOrNull<Control>("Pages/Basic/Margin/Fields/PreventAttacks") is null, "No duplicate durability or basic passive fields");
            EditorField<CheckBox>("Permanent").ButtonPressed = true;
            Require(!EditorField<LineEdit>("Energy").Editable, "Permanent field disables durability immediately");
            Require(JsonNode.Parse(ReadEditorCard().Json)!["lifetime"]!["energy"] is null, "Permanent field omits durability");
            EditorField<CheckBox>("Permanent").ButtonPressed = false;
            Require(EditorField<LineEdit>("Energy").Editable, "Finite durability re-enabled");
            var abilities = JsonNode.Parse(_editorPage.GetNode<TextEdit>("Pages/Effects").Text)!;
            Require(abilities["preventsActiveAttacksInLane"] is not null && abilities["storedCharge"] is not null, "Passive abilities editable in JSON");
            Require(_editorPage.GetNode<Button>("ExportCard").Visible && _editorPage.GetNode<Button>("ExportPack").Visible, "Visible export entries");
            await CaptureInteraction("p10-editor-field");
            var fields = _editorPage.GetNode<GridContainer>("Pages/Basic/Margin/Fields");
            Require(fields.GlobalPosition.Y - _editorPage.GetNode<ScrollContainer>("Pages/Basic").GlobalPosition.Y >= 19, "Form has top padding below tabs");
            _editorPage.GetNode<Button>("Pack").EmitSignal(BaseButton.SignalName.Pressed); await CaptureInteraction("p10-editor-pack");
            _editorPage.GetNode<Window>("PackDialog").Hide();
            GD.Print("P10_EDITOR_SMOKE_OK menu-pages=9 draft=isolated-and-retained stat-preview=verified effects=validated field=durability pack=available");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
        finally { if (savedDraft is not null) { File.WriteAllBytes(EditorDraftPath, savedDraft); } else if (File.Exists(EditorDraftPath)) { File.Delete(EditorDraftPath); } }
    }

    private async Task HostedMatchSmokeAsync(bool hosting)
    {
        var args = OS.GetCmdlineUserArgs(); var index = Array.IndexOf(args, "--invitation-file");
        var invitationFile = index >= 0 ? args[index + 1] : throw new InvalidDataException("Smoke invitation file required.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var token = deadline.Token;
        try
        {
            _catalog = new DesktopCatalog(_catalog.Root); CardTile.UseCatalog(_catalog); _contentEditor = new DesktopContentEditor(_catalog);
            if (!hosting)
            {
                const string unused = "EOTA-CORE-HUN-MIN-001";
                var card = _contentEditor.Read(unused); var json = JsonNode.Parse(card.Json)!; json["attack"] = 999999;
                _contentEditor.SaveCard(card with { Json = json.ToJsonString() }, unused);
                var path = ProjectSettings.GlobalizePath("res://artifacts/p10-join-content.eotapack.json"); _contentEditor.SavePack(path);
                _catalog = new DesktopCatalog(_catalog.Root, path); CardTile.UseCatalog(_catalog);
            }
            if (hosting)
            {
                _serverLauncher = new LocalServerLauncher(_catalog.Root, Path.Combine(_catalog.Root, "artifacts", "P10ManagedRooms"));
                _protocol = DesktopProtocol.Default.WithValues(new Dictionary<string, string>
                { ["openingHandSize"] = "40", ["handLimit"] = "40", ["initialHeroHealth"] = "8", ["mulliganEnabled"] = "false",
                    ["initialPlayerCost"] = "100", ["initialMaxCost"] = "100", ["maxCostLimit"] = "100" });
            }
            BuildMenu(); var tabs = _menuScreen.GetNode<TabContainer>("Tabs"); var remote = tabs.GetNode<Control>("Remote");
            if (hosting)
            {
                tabs.CurrentTab = 2; var host = tabs.GetNode<Control>("Host");
                host.GetNode<LineEdit>("Port").Text = "5097";
                host.GetNode<OptionButton>("Timeout").Select(1); _hostTimeout = 30;
                host.GetNode<Button>("Start").EmitSignal(BaseButton.SignalName.Pressed); await Until(() => !_busy);
                Require(_serverLauncher.State == LocalServerState.Joinable, _serverLauncher.StatusMessage);
                await CaptureInteraction("p10-host-open");
                File.WriteAllText(invitationFile, _serverLauncher.Room!.PlayerTwoEndpoint!.AbsoluteUri);
                host.GetNode<Button>("Join").EmitSignal(BaseButton.SignalName.Pressed);
            }
            else
            {
                tabs.CurrentTab = 1; remote.GetNode<LineEdit>("Address").Text = File.ReadAllText(invitationFile);
                remote.GetNode<OptionButton>("Deck").Select(1);
            }
            remote.GetNode<Button>("Connect").EmitSignal(BaseButton.SignalName.Pressed); await Until(() => !_busy);
            Require(!remote.GetNode<Button>("Ready").Disabled, _status.Text);
            remote.GetNode<Button>("ProtocolReview").EmitSignal(BaseButton.SignalName.Pressed);
            var protocolReview = remote.GetNode<Window>("RoomProtocolReview");
            Require(protocolReview.Visible && protocolReview.GetNode<VBoxContainer>("Scroll/Rows").GetChildCount() > 20, "Full protocol is reviewable before approval");
            protocolReview.GetNode<Button>("Close").EmitSignal(BaseButton.SignalName.Pressed);
            await CaptureInteraction(hosting ? "p10-room-one" : "p10-room-two");
            remote.GetNode<Button>("Ready").EmitSignal(BaseButton.SignalName.Pressed);
            await Until(() => _session is not null && _battle && !_busy);
            Require(_session!.Client.Store.View!.Audience == (hosting ? Audience.PlayerOne : Audience.PlayerTwo), "Credential binds correct seat");
            if (!hosting) { Require(_session.Client.Store.View.RuleContentHash != _catalog.RuleHash, "Unused local rule differences allow a real network match"); }
            await Until(() => _session.Client.Timer is not null); RefreshMatchClock();
            Require(_matchClockLabel.Visible && _matchClockLabel.Text.Contains("秒", StringComparison.Ordinal), "Server countdown is visible");
            await CaptureInteraction(hosting ? "p10-timed-battle-one" : "p10-timed-battle-two");
            if (hosting)
            {
                var generation = _session.ConnectionGeneration; await _session.Client.DisposeAsync();
                await Until(() => _session.ConnectionGeneration > generation && _session.ConnectionStatus.State == "Connected");
                Require(_session.Client.Store.View!.Audience == Audience.PlayerOne, "Automatic reconnect retains authenticated seat");
            }
            _busy = true;
            ObserverResumeCursor? replayCursor = null;
            while (_session.Client.Store.View!.Status == "Active")
            {
                token.ThrowIfCancellationRequested(); await _session.Client.SynchronizeAsync(token);
                var view = _session.Client.Store.View!;
                if (view.Status != "Active") { break; }
                if (view.Stage == "Mulligan") { await SubmitForSmoke(new SubmitMulliganPayload([])); continue; }
                if (view.Stage != "Planning" || view.Players[view.Private!.PlayerId].Submitted) { await Task.Delay(30, token); continue; }
                var deployments = 0;
                while (deployments++ < 6)
                {
                    var own = _session.Client.Store.View!.Private!;
                    var option = own.PlanOptions.FirstOrDefault(o => o.Allowed && own.Hand.Single(c => c.CardInstanceId == o.CardInstanceId).CardKind == "Minion");
                    if (option is null) { break; }
                    await SubmitForSmoke(new PlanCardPayload(option.CardInstanceId, option.LaneId!.Value));
                }
                replayCursor = new ObserverResumeCursor(_session.Client.Store.MatchRevision, _session.Client.Store.ObserverViewHash!);
                await SubmitForSmoke(new SubmitTurnPayload());
            }
            if (replayCursor is not null)
            {
                // Hold the scene pump while this explicit smoke request restores its batch;
                // otherwise _Process can drain it before the test's manual scene rebuild.
                SetProcess(false);
                try
                {
                    await _session.Client.ResumeAsync(replayCursor, token); BuildBattle();
                    Require(_frames.Count > 0, "Missing frames are queued for Godot presentation");
                    await CaptureInteraction(hosting ? "p10-frame-resume-one" : "p10-frame-resume-two");
                }
                finally { SetProcess(true); }
            }
            var supportPath = ProjectSettings.GlobalizePath(hosting ? "res://artifacts/p10-support-one.json" : "res://artifacts/p10-support-two.json");
            await _session.SaveDiagnosticsAsync(supportPath, token);
            Require(_battleScreen.GetNode<Button>("Toolbar/SaveDiagnostics").Visible && File.Exists(supportPath), "Support export available in battle");
            _busy = false; _frames.Clear(); BuildBattle(); await CaptureInteraction(hosting ? "p10-host-finished" : "p10-join-finished");
            var final = _session.Client.Store.View!;
            Require(final.Status == "Finished", "Network match completes");
            if (!hosting) { File.WriteAllText(invitationFile + ".done", final.Outcome); }
            else { await Until(() => File.Exists(invitationFile + ".done")); Require(File.ReadAllText(invitationFile + ".done") == final.Outcome, "Independent clients agree on result"); }
            await CloseSession();
            if (hosting)
            {
                await _serverLauncher.StopAsync(); Require(_serverLauncher.State == LocalServerState.Stopped, "Managed process stops");
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 5097);
                listener.Start(); // Proves the previous process released the port.
                try
                {
                    var blocked = await _serverLauncher.StartAsync(new LocalServerOptions("恢复验收", 5097, false, _catalog, _decks[0], _decks[1], Resume: true), token);
                    Require(!blocked.Started && _serverLauncher.State == LocalServerState.Failed, "Occupied port fails without stopping its owner");
                }
                finally { listener.Stop(); }
                // A new launcher instance loads the last room, as after restarting the client.
                await _serverLauncher.DisposeAsync();
                _serverLauncher = new LocalServerLauncher(_catalog.Root, Path.Combine(_catalog.Root, "artifacts", "P10ManagedRooms"));
                var restored = await _serverLauncher.StartAsync(new LocalServerOptions("恢复验收", 5097, false, _catalog, _decks[0], _decks[1], Resume: true), token);
                Require(restored.Started, restored.Message);
                using var client = new DesktopRoomClient(restored.Endpoint!); await client.InspectAsync(token);
                await using var resumed = await DesktopSession.JoinRoomAsync(client, token);
                Require(ObserverViewHasher.Compute(final) == resumed.Client.Store.ObserverViewHash, "Managed restart restores the exact final snapshot");
                await _serverLauncher.StopAsync();
            }
            GD.Print($"P10_HOSTING_SMOKE_OK seat={(hosting ? "one" : "two")} outcome={final.Outcome} turn={final.Turn} authentication=verified server=independent timer=visible reconnect={(hosting ? "verified" : "peer")} frame-resume=verified diagnostics=exported");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            _busy = false; GD.PushError(error.ToString()); await CloseSession(); if (hosting) { await _serverLauncher.StopAsync(); } GetTree().Quit(1);
        }
        async Task Until(Func<bool> condition) { while (!condition()) { await Task.Delay(20, token); } }
    }
}
