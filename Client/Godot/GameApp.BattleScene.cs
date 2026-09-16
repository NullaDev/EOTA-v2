using System.Collections.Immutable;
using Eota.Client.Desktop;
using Eota.Transport.Contracts;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private Control _battleScreen = null!;
    private Label _aiStatusLabel = null!;
    private Label _matchClockLabel = null!;
    private int _shownConnectionGeneration;

    private void BuildBattle(ObserverView? initialView = null)
    {
        CaptureEditorDraft();
        _shownConnectionGeneration = _session?.ConnectionGeneration ?? 0;
        var resumed = initialView is null ? _session?.Client.Store.TakeResumedPresentation() : null;
        _session?.Client.Store.DrainPresentationFrames();
        initialView ??= resumed?.BaseView;
        Ui.Clear(_root); _registry.Clear(); _slots.Clear(); _laneStates.Clear(); _etherPips.Clear(); _drops.Clear(); _frames.Clear();
        _plannedTiles.Clear(); _laneSpellPlans.Clear();
        _battle = true; _paused = false; _speed = 1; _wait = 0; _selected = null; _mulligan.Clear(); _displayed = null;
        _battleScreen = Ui.Instantiate<Control>("BattleScreen");
        _root.AddChild(_battleScreen);
        _animations = _battleScreen.GetNode<BattleAnimations>("Animations");
        _matchInfo = _battleScreen.GetNode<Label>("Toolbar/MatchInfo");
        _aiStatusLabel = _battleScreen.GetNode<Label>("AiStatus");
        _matchClockLabel = _battleScreen.GetNode<Label>("MatchClock");
        _aiStatusLabel.Visible = _session?.AiSettings is not null;
        RefreshAiStatus();
        _heroes = [_battleScreen.GetNode<HeroPanel>("Player"), _battleScreen.GetNode<HeroPanel>("Opponent")];
        _detail = _battleScreen.GetNode<VBoxContainer>("DetailPanel/Column/Detail");
        _inspectionOwner = null;
        _hand = _battleScreen.GetNode<HBoxContainer>("HandScroll/Hand");
        _plans = _battleScreen.GetNode<HBoxContainer>("Controls/PlansScroll/Plans");
        _status = _battleScreen.GetNode<Label>("Status");
        _submit = _battleScreen.GetNode<Button>("Controls/Submit");
        _replaySlider = _battleScreen.GetNode<HSlider>("DetailPanel/Column/ReplaySlider");
        _battleScreen.GetNode<Button>("Toolbar/Back").Pressed += () => Run(async () => { await CloseSession(); BuildMenu(); });
        var switchSeat = _battleScreen.GetNode<Button>("Toolbar/SwitchSeat"); switchSeat.Visible = _session?.CanSwitchSeat == true;
        switchSeat.Pressed += ShowHandoff;
        var restart = _battleScreen.GetNode<Button>("Toolbar/Restart"); restart.Visible = _session?.IsLocal == true;
        restart.Pressed += () => Run(async () =>
        {
            var previous = _session!;
            await CloseSession();
            _session = await previous.RestartAsync(_catalog); BuildBattle();
        });
        var save = _battleScreen.GetNode<Button>("Toolbar/SaveReplay"); save.Visible = _session?.IsLocal == true;
        save.Pressed += () => Run(async () =>
        {
            var directory = ProjectSettings.GlobalizePath("user://replays/" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            await _session!.SaveReplayAsync(directory); _status.Text = "回放已保存：" + directory;
        });
        var resync = _battleScreen.GetNode<Button>("Toolbar/Resync"); resync.Visible = _session is not null;
        var diagnostics = _battleScreen.GetNode<Button>("Toolbar/SaveDiagnostics"); diagnostics.Visible = _session?.CanReconnect == true;
        diagnostics.Pressed += () => Run(async () =>
        {
            var path = ProjectSettings.GlobalizePath("user://diagnostics/" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            await _session!.SaveDiagnosticsAsync(path); _status.Text = "诊断已保存：" + path;
        });
        resync.Pressed += () => Run(async () =>
        {
            var session = _session!;
            if (session.CanReconnect && (session.Client.Completion.IsCompleted || session.ConnectionStatus.State != "Connected")) { await session.ReconnectAsync(); }
            else { await session.Client.SynchronizeAsync(); }
            if (_session == session && _battle) { BuildBattle(); }
        });
        var pause = _battleScreen.GetNode<Button>("Toolbar/Pause");
        pause.Pressed += () => { _paused = !_paused; pause.Text = _paused ? "继续动画" : "暂停动画"; };
        var speed = _battleScreen.GetNode<OptionButton>("Toolbar/Speed");
        foreach (var text in new[] { "1×", "2×", "4×" }) { speed.AddItem(text); }
        speed.ItemSelected += value => _speed = 1 << (int)value;
        _battleScreen.GetNode<Button>("Toolbar/Skip").Pressed += () =>
        {
            _animations.Clear();
            _session?.Client.Store.DrainPresentationFrames(); _frames.Clear(); _wait = 0;
            if (_replay is not null) { _replayIndex = _replay.Frames.Length; _replaySlider?.SetValueNoSignal(_replayIndex); Render(_replay.Final); }
            else if (_session?.Client.Store.View is { } current) { Render(current); }
        };
        _submit.Pressed += () => Run(async () =>
        {
            var payload = _session!.Client.Store.View!.Stage == "Mulligan" ? (ClientPayload)new SubmitMulliganPayload(_mulligan.Order().ToImmutableArray()) : new SubmitTurnPayload();
            var ack = await _session.SubmitAsync(payload); _status.Text = ack.Accepted ? "已提交，等待另一位玩家。" : Ui.Reason(ack.Code);
            _mulligan.Clear();
        });
        var global = _battleScreen.GetNode<LaneDrop>("Controls/GlobalDrop");
        _globalSpellPlans = global.GetNode<SpellPlans>("Content/SpellPlans");
        _globalSpellPlans.Presentation = Presentation; _globalSpellPlans.Inspected = card => InspectFrom(card, _globalSpellPlans);
        _globalSpellPlans.InspectionEnded = () => ClearInspection(_globalSpellPlans);
        _globalSpellPlans.CancelRequested = CancelPlan;
        BindDrop(global, null);
        var initial = initialView ?? _session!.Client.Store.View!; var own = initial.Private?.PlayerId ?? 0;
        var lanes = _battleScreen.GetNode<HBoxContainer>("BoardScroll/Lanes");
        foreach (var lane in initial.Lanes)
        {
            var node = Ui.Instantiate<LaneDrop>("Lane");
            lanes.AddChild(node); BindDrop(node, lane.LaneId);
            node.GetNode<Label>("Slots/Title").Text = $"第 {lane.LaneId + 1} 路";
            _laneStates[lane.LaneId] = node.GetNode<HBoxContainer>("Slots/States");
            var spells = node.GetNode<SpellPlans>("Slots/SpellPlans");
            spells.Presentation = Presentation; spells.Inspected = card => InspectFrom(card, spells);
            spells.InspectionEnded = () => ClearInspection(spells); _laneSpellPlans[lane.LaneId] = spells;
            spells.CancelRequested = CancelPlan;
            foreach (var (prefix, player) in new[] { ("Opponent", 1 - own), ("Player", own) })
            {
                _etherPips[(lane.LaneId, player)] = node.GetNode<EtherPips>("Slots/" + prefix + "Ether");
                foreach (var kind in new[] { "Minion", "Field" })
                { _slots[(lane.LaneId, player, kind)] = node.GetNode<Control>("Slots/" + prefix + kind); }
            }
        }
        Render(initial); _revision = _session?.Client.Store.MatchRevision ?? 0;
        if (resumed is not null) { foreach (var frame in resumed.Frames) { _frames.Enqueue(frame); } }
        _submit.Disabled = initial.Private is null;
    }

    private void BindDrop(LaneDrop drop, int? lane)
    {
        drop.LaneId = lane; drop.Allowed = Allowed; drop.Dropped = Play;
        drop.Clicked = target => { if (_selected is { } id) { Play(id, target); } };
        _drops.Add(drop);
    }

    private void RefreshAiStatus()
    {
        if (_session?.AiSettings is not { } settings || _session.AiStatus is not { } status) { return; }
        var name = DesktopCatalog.AiDifficulties.Single(value => value.Id == settings.Difficulty).Name;
        var text = status.State switch
        {
            "Mulligan" => "正在换牌…", "Thinking" => "正在思考…", "Waiting" => "等待你提交",
            "Finished" => "对局结束", "Stopped" => "已停止", "Faulted" => "行动中断，请重开",
            _ => "正在准备…"
        };
        _aiStatusLabel.Text = name + " AI\n" + text;
        _aiStatusLabel.TooltipText = status.Error ?? "";
    }

    private void RefreshMatchClock()
    {
        var timer = _session?.Client.Timer;
        _matchClockLabel.Visible = _replay is null && timer is not null;
        if (timer is null) { return; }
        var seconds = timer.RemainingSeconds;
        var view = _session!.Client.Store.View!;
        var submitted = view.Private is { } own && view.Players.Single(p => p.PlayerId == own.PlayerId).Submitted;
        _matchClockLabel.Text = $"{(submitted ? "等待对方" : "提交倒计时")}\n{seconds} 秒";
        _matchClockLabel.AddThemeColorOverride("font_color", seconds <= 10 ? new Color("f28578") : new Color("c8ab6b"));
    }
}
