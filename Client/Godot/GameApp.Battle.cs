using System.Collections.Immutable;
using Eota.Client.Desktop;
using Eota.Transport.Contracts;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private CardPresentation Presentation(CardView card) => _catalog.Cards.FirstOrDefault(value => value.Id == card.PrototypeId)
        ?? new CardPresentation(card.PrototypeId, card.PrototypeId, "此卡来自服务器的其他内容包。", "icon.svg", card.CardKind, "Neutral", "Test", card.Cost, card.Attack, card.MaximumHealth, null, false);

    private CardTile Tile(CardView card, bool compact)
    {
        var tile = CardTile.CreateInstance(Presentation(card), card, compact);
        tile.Inspected = value => InspectFrom(value, tile); tile.InspectionEnded = () => ClearInspection(tile); return tile;
    }

    private Control? _inspectionOwner;
    private void InspectFrom(CardPresentation card, Control owner) { _inspectionOwner = owner; Inspect(card); }
    private void ClearInspection(Control owner)
    {
        if (_inspectionOwner != owner || !_battle) { return; }
        _inspectionOwner = null; Ui.Clear(_detail);
    }

    private void Inspect(CardPresentation card)
    {
        if (!_battle) { return; }
        Ui.Clear(_detail);
        var inspection = Ui.Instantiate<VBoxContainer>("CardInspection"); _detail.AddChild(inspection);
        inspection.GetNode<VBoxContainer>("Card").AddChild(CardTile.CreatePrototype(card));
        inspection.GetNode<Label>("RulesScroll/Rules").Text = card.Description;
    }

    private void Render(ObserverView view)
    {
        if (_inspectionOwner is { } owner && (!GodotObject.IsInstanceValid(owner) || !owner.IsInsideTree()))
        { _inspectionOwner = null; Ui.Clear(_detail); }
        _displayed = view;
        var own = view.Private?.PlayerId ?? 0;
        _matchInfo.Text = $"第 {view.Turn} 回合 · {Stage(view.Stage)} · {(view.Private is null ? "观战" : $"玩家 {own + 1}")}";
        if (view.Status != "Active") { _matchInfo.Text += " · " + Outcome(view.Outcome); }
        for (var index = 0; index < 2; index++)
        {
            var player = view.Players.Single(value => value.PlayerId == (index == 0 ? own : 1 - own));
            _heroes[index].Bind(player);
        }
        var cost = _battleScreen.GetNode<Label>("Cost");
        cost.Text = view.Private is { } privateView ? $"可用费用\n{privateView.AvailableCost} / {view.Players.Single(player => player.PlayerId == own).MaxCost}" : "观战";
        var alive = view.Entities.Select(value => value.EntityId).ToHashSet();
        foreach (var id in _registry.Keys.Where(id => !alive.Contains(id)).ToArray())
        { var node = _registry[id]; node.GetParent().RemoveChild(node); node.QueueFree(); _registry.Remove(id); }
        foreach (var entity in view.Entities)
        {
            var slot = _slots[(entity.LaneId, entity.ControllerId, entity.Card.CardKind)];
            if (!_registry.TryGetValue(entity.EntityId, out var tile) || tile.CardId != entity.Card.CardInstanceId
                || tile.GetMeta("prototype", "").AsString() != entity.Card.PrototypeId)
            {
                if (tile is not null) { tile.GetParent().RemoveChild(tile); tile.QueueFree(); }
                tile = Tile(entity.Card, true); tile.SetMeta("prototype", entity.Card.PrototypeId);
                _registry[entity.EntityId] = tile; slot.AddChild(tile);

            }
            else if (tile.GetParent() != slot) { tile.Reparent(slot, false); }
            tile.UpdateCard(entity.Card, entity);
        }
        RenderPlanning(view);
        foreach (var lane in view.Lanes)
        {
            var row = _laneStates[lane.LaneId];
            row.GetNode<TextureRect>("Frozen").Visible = lane.Frozen;
            row.GetNode<TextureRect>("Locked").Visible = lane.Locked;
            _etherPips[(lane.LaneId, 0)].Bind(0, lane.PlayerOne.EtherActivation);
            _etherPips[(lane.LaneId, 1)].Bind(1, lane.PlayerTwo.EtherActivation);
        }
        Ui.Clear(_hand); Ui.Clear(_plans);
        foreach (var card in view.Private?.Hand ?? [])
        {
            var tile = Tile(card, false); _hand.AddChild(tile);
            tile.CanDrag = () => !_busy && !Animating && _frames.Count == 0 && view.Stage == "Planning";
            tile.Clicked = id =>
            {
                if (view.Stage == "Mulligan") { if (!_mulligan.Add(id)) { _mulligan.Remove(id); } tile.Modulate = _mulligan.Contains(id) ? Ui.Accent : Colors.White; }
                else { _selected = id; Highlight(); }
            };
            if (_mulligan.Contains(card.CardInstanceId)) { tile.Modulate = Ui.Accent; }
        }
        foreach (var plan in view.Private?.Planning ?? [])
        {
            var chip = Ui.Instantiate<Button>("PlanChip"); _plans.AddChild(chip);
            chip.Text = $"{Presentation(plan.Card).Name} → {(plan.LaneId is { } lane ? $"路 {lane + 1}" : "全局")} ×";
            chip.Disabled = view.Stage != "Planning" || view.Players.Single(player => player.PlayerId == own).Submitted;
            chip.Pressed += () => CancelPlan(plan.PlanCommandId);
        }
        _submit.Text = view.Stage == "Mulligan" ? $"确认换牌（{_mulligan.Count} 张）" : "提交回合";
        Highlight();
        if (_inspectionOwner is { } previous && (!GodotObject.IsInstanceValid(previous) || !previous.IsInsideTree()))
        { _inspectionOwner = null; Ui.Clear(_detail); }
    }

    private bool Allowed(ulong card, int? lane) => !_busy && !Animating && _frames.Count == 0 && _session?.Client.Store.NeedsSnapshot == false
        && _session.Client.Store.View?.Private?.PlanOptions.Any(value => value.CardInstanceId == card && value.LaneId == lane && value.Allowed) == true;

    private void Highlight()
    {
        foreach (var drop in _drops)
        {
            var option = _session?.Client.Store.View?.Private?.PlanOptions.FirstOrDefault(value => value.CardInstanceId == _selected && value.LaneId == drop.LaneId);
            var panel = (StyleBoxFlat)drop.GetThemeStylebox("panel").Duplicate();
            panel.BorderColor = option?.Allowed == true ? Ui.Accent : new Color("505b45");
            drop.AddThemeStyleboxOverride("panel", panel);
            drop.TooltipText = option is null ? "" : Ui.Reason(option.Reason);
        }
    }

    private void Play(ulong card, int? lane)
    {
        if (!Allowed(card, lane))
        { _status.Text = Ui.Reason(_session?.Client.Store.View?.Private?.PlanOptions.FirstOrDefault(value => value.CardInstanceId == card && value.LaneId == lane)?.Reason ?? "此处无法放置"); return; }
        Run(async () =>
        {
            var session = _session!;
            var value = session.Client.Store.View!.Private!.Hand.Single(value => value.CardInstanceId == card);
            var payload = value.CardKind == "Spell" ? (ClientPayload)new PlanSpellPayload(card, lane) : new PlanCardPayload(card, lane!.Value);
            var ack = await session.SubmitAsync(payload); await session.Client.SynchronizeAsync();
            if (_battle && _session == session)
            { _selected = null; Render(session.Client.Store.View!); _status.Text = ack.Accepted ? "已规划，可继续出牌或撤回。" : Ui.Reason(ack.Code); }
        });
    }

    public override void _Process(double delta)
    {
        if (_battle && !_paused && GodotObject.IsInstanceValid(_animations)) { _animations.Advance(delta * _speed); }
        if (_battle && _replay is not null) { ProcessReplay(delta); return; }
        if (!_battle || _session is null) { return; }
        RefreshMatchClock();
        if (_session.ConnectionGeneration != _shownConnectionGeneration)
        {
            var paused = _paused; var speed = _speed; BuildBattle(); _paused = paused; _speed = speed;
            _battleScreen.GetNode<Button>("Toolbar/Pause").Text = paused ? "继续动画" : "暂停动画";
            _battleScreen.GetNode<OptionButton>("Toolbar/Speed").Select(speed == 4 ? 2 : speed == 2 ? 1 : 0);
            _status.Text = "连接已恢复，已同步当前战场。"; return;
        }
        if (_session.CanReconnect && _session.ConnectionStatus.State != "Connected")
        {
            _animations.Clear(); _frames.Clear(); _selected = null; _submit.Disabled = true;
            _status.Text = _session.ConnectionStatus.Message; return;
        }
        RefreshAiStatus();
        _session.DiscardInactiveFrames();
        var store = _session.Client.Store;
        foreach (var frame in store.DrainPresentationFrames()) { _frames.Enqueue(frame); }
        if (_frames.Count > 256) { _animations.Clear(); _frames.Clear(); _wait = 0; if (store.View is { } current) { Render(current); } }
        if (store.NeedsSnapshot && !_syncing && !_busy && !_session.Client.IsClosed)
        {
            var session = _session; var client = session.Client;
            _syncing = true;
            Run(async () =>
            {
                try
                {
                    await client.SynchronizeAsync();
                    if (_battle && _session == session && session.Client == client) { BuildBattle(); }
                }
                catch (Exception error) when ((_session != session || session.Client != client || client.IsClosed)
                    && error is ObjectDisposedException or OperationCanceledException or IOException) { }
                finally { _syncing = false; }
            });
            return;
        }
        if (_session.Client.Completion.IsCompleted && store.View?.Status == "Active")
        { _status.Text = _session.CanReconnect ? "连接中断，正在重新连接…" : "连接已断开，请返回大厅重新连接。"; }
        if (!_paused && !Animating)
        {
            _wait -= delta * _speed;
            if (_wait <= 0 && _frames.TryDequeue(out var frame)) { Present(frame); _wait = frame.Events.Length == 0 ? 0 : 0.22; }
            if (!Animating && _frames.Count == 0 && _wait <= 0 && (_revision != store.MatchRevision || !ReferenceEquals(_displayed, store.View)))
            { if (store.View is { } current) { Render(current); } _revision = store.MatchRevision; }
        }
        var view = store.View;
        _submit.Disabled = _busy || Animating || _frames.Count > 0 || store.NeedsSnapshot || view?.Private is null || view.Status != "Active"
            || view.Stage is not ("Mulligan" or "Planning") || view.Players.Single(player => player.PlayerId == view.Private.PlayerId).Submitted;
        if (view?.Stage == "Mulligan") { _submit.Text = $"确认换牌（{_mulligan.Count} 张）"; }
    }

    private void Present(PresentationFramePayload frame)
    {
        var oldPositions = _registry.ToDictionary(pair => pair.Key, pair => pair.Value.GlobalPosition);
        foreach (var value in frame.Events.Where(value => value.Kind is "EntityLeft" or "EntityDied" or "EntityBanished"))
        { if (value.EntityId is { } id && oldPositions.TryGetValue(id, out var position)) { FloatText("离场", position, new Color("e5b5aa")); } }
        Render(frame.View);
        foreach (var value in frame.Events)
        {
            if (value.Kind == "HeroHealed" && value.PlayerId is { } healedPlayer && value.CurrentValue is > 0)
            {
                var own = frame.View.Private?.PlayerId ?? 0;
                var hero = _heroes[healedPlayer == own ? 0 : 1];
                FloatText("治疗 +" + value.CurrentValue, hero.GetGlobalRect().GetCenter(), Ui.Accent);
            }
            if (value.EntityId is { } id && _registry.TryGetValue(id, out var tile))
            {
                var color = value.Kind == "EntityHealed" ? Ui.Accent : value.Kind is "EntityDamaged" or "EntityHealthLost" ? new Color("f28578") : new Color("b8d8ee");
                if (value.Kind is "EntityDamaged" or "EntityHealthLost" or "EntityHealed")
                { tile.Modulate = color; tile.CreateTween().TweenProperty(tile, "modulate", Colors.White, 0.2 / _speed); }
                if (value.Kind == "EntityMoved" && oldPositions.TryGetValue(id, out var old)) { FloatText("移动 →", old, Ui.Accent); }
                if (value.PreviousValue is { } before && value.CurrentValue is { } after && before != after)
                { FloatText((after > before ? "+" : "") + (after - before), tile.GlobalPosition + new Vector2(30, 40), color); }
            }
            if (value.Kind == "MatchEnded") { _status.Text = Outcome(frame.View.Outcome); }
        }
        AnimateFrame(frame);
    }

    private void FloatText(string text, Vector2 position, Color color)
    {
        _animations.Float(text, position, color);
    }
    private static string Stage(string stage) => stage switch { "Planning" => "规划", "Mulligan" => "换牌", "Combat" => "战斗", "Movement" => "移动", "Deployment" => "部署", "Cleanup" => "清理", _ => "结算" };
    private static string Outcome(string outcome) => outcome switch { "PlayerOneWon" => "玩家一获胜", "PlayerTwoWon" => "玩家二获胜", "Draw" => "平局", "RuleFailure" => "规则执行失败", _ => outcome };
}
