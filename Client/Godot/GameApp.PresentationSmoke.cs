using System.Collections.Immutable;
using Eota.Client.Desktop;
using Eota.Transport.Contracts;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private async Task PresentationSmokeAsync()
    {
        try
        {
            _busy = true;
            var protocol = DesktopProtocol.Default.WithValues(new Dictionary<string, string>
            {
                ["openingHandSize"] = "20",
                ["handLimit"] = "40",
                ["initialPlayerCost"] = "100",
                ["initialMaxCost"] = "100",
                ["maxCostLimit"] = "100",
                ["mulliganEnabled"] = "false"
            });
            _session = await DesktopSession.LocalAsync(_catalog, _decks[0], _decks[0], new LocalMatchSettings(ProtocolJson: protocol.Json));
            for (var turn = 0; turn < 3; turn++)
            {
                for (var seat = 0; seat < 2; seat++)
                {
                    var view = _session.Client.Store.View!;
                    var choices = from card in view.Private!.Hand
                                  where card.CardKind == "Minion" && card.Attack > 0
                                  from option in view.Private.PlanOptions
                                  where option.CardInstanceId == card.CardInstanceId && option.Allowed && option.LaneId is 0 or 1
                                  orderby view.Entities.Any(entity => entity.ControllerId != seat && entity.LaneId == option.LaneId && entity.Card.CardKind == "Minion") descending,
                                      card.MaximumHealth descending, option.LaneId
                                  select (card, option);
                    if (choices.FirstOrDefault() is { card: { } selectedCard, option: { } selectedOption })
                    { await SubmitForSmoke(new PlanCardPayload(selectedCard.CardInstanceId, selectedOption.LaneId!.Value)); }
                    if (turn == 0)
                    {
                        var hand = _session.Client.Store.View!.Private!; var openLane = seat == 0 ? 3 : 5;
                        var extra = hand.Hand.Where(card => card.CardKind == "Minion" && card.Attack > 0
                            && !card.Keywords.Any(keyword => keyword.Kind is "Guard" or "Slow")
                            && hand.PlanOptions.Any(option => option.CardInstanceId == card.CardInstanceId && option.LaneId == openLane && option.Allowed))
                            .OrderByDescending(card => card.Keywords.Any(keyword => keyword.Kind == "Swift")).First();
                        await SubmitForSmoke(new PlanCardPayload(extra.CardInstanceId, openLane));
                    }
                    await SubmitForSmoke(new SubmitTurnPayload());
                    _session.SwitchSeat(); await _session.Client.SynchronizeAsync();
                }
            }
            var path = ProjectSettings.GlobalizePath("res://artifacts/P96PresentationReplay");
            await _session.SaveReplayAsync(path); var hash = _session.FinalStateHash;
            var replay = DesktopReplay.Load(_catalog, Path.Combine(path, "replay.json"), Audience.PlayerOne);
            Require(replay.StateHash == hash, "Animation fixture replay hash");
            await CloseSession(); BuildBattle(replay.Initial); _paused = true;
            var draw = replay.Frames.First(frame => frame.Events.Any(value => value.Kind == "CardDrawn" && value.Card is not null));
            Present(draw); _animations.Advance(0.16); Require(Animating, "Draw creates a visible flight");
            Require(_animations.GetChildren().OfType<CardTile>().Any(tile => tile.IsVisibleInTree()), "Own draw shows its projected face");
            var positions = _animations.GetChildren().OfType<Control>().Select(node => node.GlobalPosition).ToArray();
            _Process(0.2);
            Require(positions.SequenceEqual(_animations.GetChildren().OfType<Control>().Select(node => node.GlobalPosition)), "Paused animation stays in place");
            await CaptureInteraction("draw-flight");
            _animations.Clear(); Require(!Animating && _hand.GetChildren().OfType<CardTile>().All(tile => tile.Modulate.A == 1), "Clear restores drawn card");

            var hiddenReplay = DesktopReplay.Load(_catalog, Path.Combine(path, "replay.json"));
            var hiddenDraw = hiddenReplay.Frames.First(frame => frame.Events.Any(value => value.Kind == "CardDrawn"));
            Present(hiddenDraw); _animations.Advance(0.2);
            Require(Animating && !_animations.GetChildren().OfType<CardTile>().Any(), "Hidden draws animate only card backs");
            await CaptureInteraction("hidden-draw"); _animations.Clear();

            var attack = replay.Frames.First(frame => frame.Events.Any(value => value.Kind == "AttackDeclared" && value.TargetEntityId is not null));
            Render(attack.View); await CaptureInteraction("before-collision");
            Present(attack); _animations.Advance(0.22);
            var attackers = attack.Events.Where(value => value.Kind == "AttackDeclared" && value.EntityId is not null).Select(value => value.EntityId!.Value).ToArray();
            Require(Animating && attackers.Any(id => _registry[id].Modulate.A == 0), "Collision conceals stationary originals");
            Require(_animations.GetChildren().OfType<CardTile>().Any(ghost => _registry.Values.Any(tile => tile.CardId == ghost.CardId && tile.GlobalPosition.DistanceTo(ghost.GlobalPosition) > 20)), "Attackers lunge toward their targets");
            CheckVerticalAttacks(attack.View);
            await CaptureInteraction("collision");
            _paused = false; _speed = 4; _Process(0.2);
            Require(!Animating && _registry.Values.All(tile => tile.Modulate.A == 1), "Speed advances and finishes collisions");
            _paused = true; Present(attack); _animations.Advance(0.1);
            _battleScreen.GetNode<Button>("Toolbar/Skip").EmitSignal(BaseButton.SignalName.Pressed);
            Require(!Animating && _registry.Values.All(tile => tile.Modulate.A == 1), "Skip cancels animation and restores originals");
            foreach (var audience in new[] { Audience.PlayerOne, Audience.PlayerTwo })
            {
                var perspective = DesktopReplay.Load(_catalog, Path.Combine(path, "replay.json"), audience);
                var heroAttack = perspective.Frames.First(frame => frame.Events.Any(value => value.Kind == "AttackDeclared" && value.TargetPlayerId is not null));
                BuildBattle(heroAttack.View); _paused = true;
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                Present(heroAttack); _animations.Advance(0.22); CheckVerticalAttacks(heroAttack.View);
                await CaptureInteraction("hero-attack-" + audience); _animations.Clear();
            }
            await CheckStatColors(attack.View);
            _replay = replay; _replayIndex = replay.Frames.Length - 1; _paused = false; _speed = 4;
            for (var step = 0; step < 30; step++) { _Process(0.2); }
            Require(ReferenceEquals(_displayed, replay.Final), "Replay presents final state after animations");
            Require(DesktopReplay.Load(_catalog, Path.Combine(path, "replay.json")).StateHash == hash, "Presentation preserves authority");
            GD.Print("P96_PRESENTATION_SMOKE_OK draw=face-or-back collision=vertical hero-attack=both-seats stats=damage-priority-and-reset pause=frozen speed=4 skip=restored replay=verified");
            await CloseSession(); GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); await CloseSession(); GetTree().Quit(1); }
    }

    private void CheckVerticalAttacks(ObserverView view)
    {
        var ghosts = _animations.GetChildren().OfType<CardTile>().ToArray();
        Require(ghosts.Length > 0, "Attack frame creates animated minions");
        foreach (var ghost in ghosts)
        {
            var entity = view.Entities.Single(entity => entity.Card.CardInstanceId == ghost.CardId);
            var origin = _registry[entity.EntityId].GetGlobalRect().GetCenter();
            var center = ghost.GetGlobalRect().GetCenter();
            Require(Mathf.Abs(center.X - origin.X) < 1, "Attack stays on the lane's vertical axis");
            Require((center.Y - origin.Y) * (entity.ControllerId == view.Private!.PlayerId ? -1 : 1) > 20, "Attack direction follows displayed side");
        }
    }

    private async Task CheckStatColors(ObserverView view)
    {
        var original = view.Entities.First(entity => entity.Card.CardKind == "Minion" && Presentation(entity.Card).Attack > 0);
        var prototype = Presentation(original.Card);
        var baseAttack = prototype.Attack!.Value; var baseHealth = prototype.Health!.Value;
        var visual = view with
        {
            Entities = Enumerable.Range(0, 3).Select(index => original with
            {
                EntityId = (ulong)(10001 + index),
                LaneId = index,
                OwnerId = 0,
                ControllerId = 0,
                Card = original.Card with { CardInstanceId = (ulong)(10001 + index), Attack = baseAttack + 2, MaximumHealth = baseHealth + 2 },
                Attack = index == 0 ? baseAttack - 1 : index == 1 ? baseAttack + 1 : baseAttack,
                CurrentHealth = index == 0 ? baseHealth + 1 : index == 1 ? baseHealth + 2 : baseHealth,
                MaximumHealth = index < 2 ? baseHealth + 2 : baseHealth
            }).ToImmutableArray()
        };
        BuildBattle(visual); _paused = true;
        foreach (var path in new[] { "AttackBox/Value", "HealthBox/Value" })
        {
            var reduced = _registry[10001].GetNode<Label>(path); var increased = _registry[10002].GetNode<Label>(path);
            Require(reduced.GetThemeColor("font_color") == reduced.GetThemeColor("stat_reduced"), "Wounded buffed health and reduced attack are red");
            Require(increased.GetThemeColor("font_color") == increased.GetThemeColor("stat_increased"), "Full buffed health and raised attack are green");
            Require(!_registry[10003].GetNode<Label>(path).HasThemeColorOverride("font_color"), "Original stats use normal color");
        }
        await CaptureInteraction("stat-colors");
        var reused = _registry[10001];
        Render(visual with { Entities = visual.Entities.Select(entity => entity with { Attack = baseAttack, CurrentHealth = baseHealth, MaximumHealth = baseHealth }).ToImmutableArray() });
        Require(_registry[10001] == reused && !reused.GetNode<Label>("AttackBox/Value").HasThemeColorOverride("font_color")
            && !reused.GetNode<Label>("HealthBox/Value").HasThemeColorOverride("font_color"), "Existing tile resets after healing or buff expiry");
        var hand = CardTile.CreateInstance(prototype, original.Card with { Attack = baseAttack + 1, MaximumHealth = baseHealth + 1 }, false);
        Require(hand.GetNode<Label>("HealthBox/Value").GetThemeColor("font_color") == hand.GetNode<Label>("HealthBox/Value").GetThemeColor("stat_increased"), "Hand buffs compare with original prototype"); hand.Free();
        var catalogTile = CardTile.CreatePrototype(prototype);
        Require(!catalogTile.GetNode<Label>("HealthBox/Value").HasThemeColorOverride("font_color"), "Library prototypes stay unmodified"); catalogTile.Free();
    }
}
