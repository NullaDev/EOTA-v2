using Eota.Client.Desktop;
using Eota.Transport.Contracts;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private CancellationTokenSource? _roomWait;
    private DesktopRoomClient? _roomClient;
    private string _invitationText = "";
    private int _remoteDeck;
    private int _hostTimeout;
    private void BuildHostedServer(TabContainer tabs, List<Label> summaries)
    {
        var host = tabs.GetNode<Control>("Host"); var remote = tabs.GetNode<Control>("Remote");
        var one = host.GetNode<OptionButton>("One"); var deckChoice = remote.GetNode<OptionButton>("Deck");
        var remoteDecks = _decks.ToList();
        var timeoutChoice = host.GetNode<OptionButton>("Timeout");
        foreach (var seconds in new[] { 0, 30, 60, 120, 180 }) { timeoutChoice.AddItem(seconds == 0 ? "不限时" : $"{seconds} 秒", seconds); }
        timeoutChoice.Select(timeoutChoice.GetItemIndex(_hostTimeout));
        timeoutChoice.ItemSelected += _ => _hostTimeout = timeoutChoice.GetSelectedId();
        foreach (var deck in _decks)
        {
            var title = $"{(deck.Name == deck.Profession ? "默认牌组" : deck.Name)} · {Ui.Profession(deck.Profession)}";
            one.AddItem(title); deckChoice.AddItem(title);
        }
        one.Select(0); deckChoice.Select(Math.Min(_remoteDeck, _decks.Count - 1));
        var summary = host.GetNode<Label>("ProtocolSummary"); summary.Text = _protocol.Summary; summaries.Add(summary);
        host.GetNode<Button>("Protocol").Pressed += () => { _setupTab = 2; tabs.CurrentTab = 5; };
        var address = remote.GetNode<LineEdit>("Address"); address.Text = _invitationText;
        var connect = remote.GetNode<Button>("Connect"); var ready = remote.GetNode<Button>("Ready"); var cancel = remote.GetNode<Button>("Cancel");
        ready.Disabled = true; cancel.Disabled = true;
        var protocolReview = Ui.Instantiate<Window>("RoomProtocolReview");
        remote.AddChild(protocolReview); protocolReview.CloseRequested += protocolReview.Hide;
        protocolReview.GetNode<Button>("Close").Pressed += protocolReview.Hide;
        remote.GetNode<Button>("ProtocolReview").Pressed += () =>
        {
            if (_roomClient?.Description is not { } room) { return; }
            var rows = protocolReview.GetNode<VBoxContainer>("Scroll/Rows"); Ui.Clear(rows);
            var timingRow = Ui.Instantiate<HBoxContainer>("RoomProtocolRow"); rows.AddChild(timingRow);
            timingRow.GetNode<Label>("Label").Text = "提交时限"; timingRow.GetNode<Label>("Value").Text = TimingSummary(room.Timing);
            foreach (var field in DesktopProtocol.Parse(room.ProtocolJson).Fields.Where(f => f.Group != "版本与结算限制"))
            {
                var row = Ui.Instantiate<HBoxContainer>("RoomProtocolRow"); rows.AddChild(row);
                row.GetNode<Label>("Label").Text = field.Label;
                var value = row.GetNode<Label>("Value");
                value.Text = field.Kind == "boolean" ? bool.Parse(field.Value) ? "开启" : "关闭" : field.Choices.FirstOrDefault(c => c.Value == field.Value)?.Label ?? field.Value;
            }
            protocolReview.PopupCentered();
        };
        address.TextChanged += value => { _invitationText = value; ready.Disabled = true; _roomClient?.Dispose(); _roomClient = null; };
        deckChoice.ItemSelected += value => { _remoteDeck = (int)value; };
        remote.GetNode<Button>("HostEntry").Pressed += () => tabs.CurrentTab = 2;
        connect.Pressed += () => Run(async () =>
        {
            ready.Disabled = true; remote.GetNode<Button>("ProtocolReview").Disabled = true;
            _roomClient?.Dispose(); _roomClient = new DesktopRoomClient(new Uri(address.Text.Trim()));
            var room = await _roomClient.InspectAsync();
            var protocol = DesktopProtocol.Parse(room.ProtocolJson);
            remote.GetNode<Button>("ProtocolReview").Disabled = false;
            var preview = remote.GetNode<Label>("Compatibility");
            preview.Text = $"{room.Name} · {_roomClient.Endpoint.Host}:{_roomClient.Endpoint.Port} · 准备时核对双方牌组规则\n{protocol.Summary}\n{TimingSummary(room.Timing)}\n玩家一：{(room.PlayerOneReady ? "已确认" : "待确认")}　玩家二：{(room.PlayerTwoReady ? "已确认" : "待确认")}";
            preview.TooltipText = $"卡牌规则：{room.RuleContentHash}\n对战协议：{room.ProtocolHash}";
            ready.Text = _roomClient.IsSpectator ? "加入观战" : "确认协议与牌组"; ready.Disabled = false;
            deckChoice.Disabled = _roomClient.IsSpectator;
            if (room.SuggestedDeck is { } suggested)
            {
                var deck = new DesktopDeck(room.OwnDeckHash is null ? "房主建议" : "本局已锁定", suggested.Profession,
                    [.. suggested.Cards.Select(c => new DeckCard(c.Id, c.Copies))]);
                if (remoteDecks.Count > _decks.Count) { remoteDecks.RemoveAt(remoteDecks.Count - 1); deckChoice.RemoveItem(deckChoice.ItemCount - 1); }
                remoteDecks.Add(deck); deckChoice.AddItem(deck.Name + " · " + Ui.Profession(deck.Profession));
                if (room.OwnDeckHash is not null) { deckChoice.Select(remoteDecks.Count - 1); deckChoice.Disabled = true; }
            }
            _status.Text = "请核对房间协议，确认后将锁定本局牌组。";
        });
        ready.Pressed += () => Run(async () =>
        {
            var client = _roomClient ?? throw new InvalidOperationException("请先核对房间。");
            _roomWait?.Dispose(); _roomWait = new CancellationTokenSource(); var token = _roomWait.Token;
            address.Editable = false; connect.Disabled = true; ready.Disabled = true; deckChoice.Disabled = true; cancel.Disabled = false;
            try
            {
                if (!client.IsSpectator) { await client.ReadyAsync(remoteDecks[deckChoice.Selected], _catalog, token); }
                while (!(await client.InspectAsync(token)).Started)
                {
                    _status.Text = client.IsSpectator ? "等待双方确认牌组…" : "牌组校验通过并已锁定，等待另一位玩家确认…";
                    await Task.Delay(1000, token);
                }
                await CloseSession(); _session = await DesktopSession.JoinRoomAsync(client, token); _setupTab = 1; BuildBattle();
            }
            catch (OperationCanceledException) { _status.Text = "已取消等待；已确认的牌组仍保留在房间中。"; }
            finally
            {
                if (GodotObject.IsInstanceValid(remote) && remote.IsInsideTree())
                { address.Editable = true; connect.Disabled = false; ready.Disabled = false; deckChoice.Disabled = client.IsSpectator; cancel.Disabled = true; }
            }
        });
        cancel.Pressed += () => _roomWait?.Cancel();
        tabs.TabChanged += tab => { if (tab != 1) { _roomWait?.Cancel(); } };
        void Launch(bool resume) => Run(async () =>
        {
            if (!int.TryParse(host.GetNode<LineEdit>("Port").Text, out var port)) { throw new InvalidDataException("请输入有效端口。"); }
            _status.Text = "正在启动服务器…";
            var result = await _serverLauncher.StartAsync(new LocalServerOptions(host.GetNode<LineEdit>("MatchName").Text, port,
                host.GetNode<CheckBox>("Lan").ButtonPressed, _catalog, _decks[one.Selected], null, _protocol.Json, Resume: resume,
                Timing: new RoomTiming(_hostTimeout, _hostTimeout)));
            _status.Text = result.Message; RefreshHost();
        });
        host.GetNode<Button>("Start").Pressed += () => Launch(false);
        host.GetNode<Button>("Resume").Pressed += () => Launch(true);
        host.GetNode<Button>("Stop").Pressed += () => Run(async () => { await _serverLauncher.StopAsync(); RefreshHost(); });
        host.GetNode<Button>("Join").Pressed += () =>
        {
            if (_serverLauncher.Room?.Endpoint is not { } endpoint) { return; }
            address.Text = endpoint.AbsoluteUri; _invitationText = address.Text; deckChoice.Select(one.Selected); _remoteDeck = one.Selected; tabs.CurrentTab = 1;
        };
        void Copy(string button, Func<Uri?> value) => host.GetNode<Button>(button).Pressed += () =>
        {
            if (value() is { } uri) { DisplayServer.ClipboardSet(uri.AbsoluteUri); _status.Text = "邀请地址已复制。"; }
        };
        Copy("CopyOne", () => _serverLauncher.Room?.Endpoint); Copy("CopyTwo", () => _serverLauncher.Room?.PlayerTwoEndpoint); Copy("CopySpectator", () => _serverLauncher.Room?.SpectatorEndpoint);
        var timer = new Godot.Timer { WaitTime = 0.3, Autostart = true }; host.AddChild(timer); timer.Timeout += RefreshHost; RefreshHost();
        void RefreshHost()
        {
            var state = _serverLauncher.State; var running = state == LocalServerState.Joinable; var changing = state is LocalServerState.Starting or LocalServerState.Stopping;
            host.GetNode<Label>("Availability").Text = _serverLauncher.IsAvailable ? _serverLauncher.StatusMessage : "缺少服务器程序";
            host.GetNode<Label>("Notice").Text = running ? $"房间：{_serverLauncher.Room!.MatchId}\n地址：{_serverLauncher.Room.Endpoint!.Host}:{_serverLauncher.Room.Endpoint.Port} · 邀请按钮会复制完整凭据"
                : _serverLauncher.IsAvailable ? "对方加入后选择并提交自己的牌组。停止服务器会断开房间中的所有玩家。" : "请使用包含服务器组件的完整游戏版本。";
            host.GetNode<Button>("Start").Disabled = !_serverLauncher.IsAvailable || running || changing;
            host.GetNode<Button>("Resume").Disabled = !_serverLauncher.IsAvailable || running || changing || _serverLauncher.Room is null;
            host.GetNode<Button>("Protocol").Disabled = running || changing;
            timeoutChoice.Disabled = running || changing;
            host.GetNode<Button>("Stop").Disabled = !running;
            foreach (var button in new[] { "Join", "CopyOne", "CopyTwo", "CopySpectator" }) { host.GetNode<Button>(button).Disabled = !running; }
            one.Disabled = running || changing; host.GetNode<LineEdit>("MatchName").Editable = !running && !changing;
            host.GetNode<LineEdit>("Port").Editable = !running && !changing; host.GetNode<CheckBox>("Lan").Disabled = running || changing;
        }
    }

    private static string TimingSummary(RoomTiming? timing)
    {
        static string Limit(int value) => value == 0 ? "不限时" : $"{value} 秒";
        return $"换牌：{Limit(timing?.MulliganSeconds ?? 0)} · 每回合：{Limit(timing?.PlanningSeconds ?? 0)}";
    }
}
