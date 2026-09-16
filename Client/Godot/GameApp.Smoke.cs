using Eota.Client.Desktop;
using Eota.Transport.Contracts;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private async Task SmokeAsync()
    {
        try
        {
            _session = await DesktopSession.LocalAsync(_catalog, _decks[0], _decks[1], new LocalMatchSettings(Mulligan: false));
            BuildBattle(); _busy = true;
            for (var turn = 0; turn < 4; turn++)
            {
                for (var seat = 0; seat < 2; seat++)
                {
                    var own = _session.Client.Store.View!.Private!;
                    var option = own.PlanOptions.FirstOrDefault(value => value.Allowed);
                    if (option is not null)
                    {
                        var card = own.Hand.Single(value => value.CardInstanceId == option.CardInstanceId);
                        var payload = card.CardKind == "Spell" ? (ClientPayload)new PlanSpellPayload(card.CardInstanceId, option.LaneId) : new PlanCardPayload(card.CardInstanceId, option.LaneId!.Value);
                        var ack = await SubmitForSmoke(payload);
                        if (turn == 0 && seat == 0)
                        {
                            await SubmitForSmoke(new CancelPlanPayload(ack.PlanCommandId!.Value));
                            await SubmitForSmoke(payload);
                        }
                    }
                    await SubmitForSmoke(new SubmitTurnPayload());
                    _session.SwitchSeat(); await _session.Client.SynchronizeAsync();
                }
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            _busy = false;
            if (_session.Client.Store.View!.Turn != 5) { throw new InvalidOperationException("Four completed turns must reach turn 5."); }
            _frames.Clear(); BuildBattle(); Render(_session.Client.Store.View!);
            if (_registry.Count != _session.Client.Store.View!.Entities.Length) { throw new InvalidOperationException("registry mismatch"); }
            var hash = _session.FinalStateHash;
            var replayDirectory = ProjectSettings.GlobalizePath("res://artifacts/P9GodotReplay");
            await _session.SaveReplayAsync(replayDirectory);
            var replay = DesktopReplay.Load(_catalog, Path.Combine(replayDirectory, "replay.json"));
            if (replay.StateHash != hash) { throw new InvalidOperationException("replay mismatch"); }
            BuildBattle(); _paused = true; _speed = 4; _frames.Clear();
            if (_session.FinalStateHash != hash) { throw new InvalidOperationException("presentation changed authority"); }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var path = ProjectSettings.GlobalizePath("res://artifacts/p9-battle.png");
                GetViewport().GetTexture().GetImage().SavePng(path);
            }
            GD.Print($"P9_GODOT_SMOKE_OK cards={_catalog.Cards.Length} entities={_registry.Count} turn={_session.Client.Store.View!.Turn} state={hash}");
            await CloseSession(); GetTree().Quit();
        }
        catch (Exception error) { _busy = false; GD.PushError(error.ToString()); await CloseSession(); GetTree().Quit(1); }
    }

    private async Task<CommandAckPayload> SubmitForSmoke(ClientPayload payload)
    {
        var ack = await _session!.SubmitAsync(payload);
        if (!ack.Accepted) { throw new InvalidOperationException($"{payload.GetType().Name}: {ack.Code}"); }
        // Acknowledgements precede view updates; wait before issuing the next automated command.
        await _session.Client.SynchronizeAsync();
        return ack;
    }
}
