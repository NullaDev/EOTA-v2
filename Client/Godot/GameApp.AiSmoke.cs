using Eota.Client.Desktop;
using Eota.Transport.Contracts;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private async Task AiSmokeAsync()
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            _protocol = DesktopProtocol.Default;
            BuildMenu();
            for (var difficultyIndex = 0; difficultyIndex < DesktopCatalog.AiDifficulties.Length; difficultyIndex++)
            {
                var local = _menuScreen.GetNode<Control>("Tabs/Ai");
                var difficulty = local.GetNode<OptionButton>("Difficulty");
                if (difficulty.ItemCount != 3 || local.GetNode<Button>("Start").Disabled) { throw new InvalidOperationException("AI setup is unavailable."); }
                difficulty.Select(difficultyIndex); difficulty.EmitSignal(OptionButton.SignalName.ItemSelected, (long)difficultyIndex);
                if (difficultyIndex == 1) { await CaptureAi("setup"); }
                local.GetNode<Button>("Start").EmitSignal(BaseButton.SignalName.Pressed);
                await Until(() => _session is not null && !_busy);
                var session = _session!;
                if (session.AiSettings?.Difficulty != DesktopCatalog.AiDifficulties[difficultyIndex].Id || session.CanSwitchSeat
                    || _battleScreen.GetNode<Button>("Toolbar/SwitchSeat").Visible)
                { throw new InvalidOperationException("AI difficulty or fixed human seat mismatch."); }
                _busy = true;
                await SubmitForSmoke(new SubmitMulliganPayload([session.Client.Store.View!.Private!.Hand[0].CardInstanceId]));
                var captured = false; var cancelled = false;
                while (session.Client.Store.View!.Status == "Active" && session.Client.Store.View.Turn <= 100)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (session.AiStatus!.State == "Faulted") { throw new InvalidOperationException(session.AiStatus.Error); }
                    await session.Client.SynchronizeAsync(deadline.Token);
                    var view = session.Client.Store.View!;
                    if (view.Stage != "Planning" || view.Players[0].Submitted)
                    { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); continue; }
                    var option = view.Private!.PlanOptions.FirstOrDefault(value => value.Allowed);
                    if (option is not null)
                    {
                        var card = view.Private.Hand.Single(value => value.CardInstanceId == option.CardInstanceId);
                        var payload = card.CardKind == "Spell" ? (ClientPayload)new PlanSpellPayload(card.CardInstanceId, option.LaneId)
                            : new PlanCardPayload(card.CardInstanceId, option.LaneId!.Value);
                        var ack = await SubmitForSmoke(payload);
                        if (!cancelled)
                        {
                            await SubmitForSmoke(new CancelPlanPayload(ack.PlanCommandId!.Value));
                            await SubmitForSmoke(payload); cancelled = true;
                        }
                    }
                    if (!captured && view.Turn >= 4)
                    {
                        BuildBattle(); _busy = false; await CaptureAi("battle-" + difficultyIndex); CheckLayout(); _busy = true; captured = true;
                    }
                    await SubmitForSmoke(new SubmitTurnPayload());
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }
                if (session.Client.Store.View!.Status != "Finished" || session.AiStatus!.RejectedCommands != 0)
                { throw new InvalidOperationException("AI match failed to finish cleanly."); }
                var directory = ProjectSettings.GlobalizePath("res://artifacts/P95GodotReplay/" + session.AiSettings!.Difficulty);
                await session.SaveReplayAsync(directory);
                var replay = await Task.Run(() => DesktopReplay.Load(_catalog, Path.Combine(directory, "replay.json")), deadline.Token);
                if (replay.StateHash != session.FinalStateHash) { throw new InvalidOperationException("AI replay mismatch."); }
                BuildBattle(); _busy = false; await CaptureAi("result-" + difficultyIndex);
                GD.Print($"P95_AI_MATCH_OK difficulty={session.AiSettings.Difficulty} turn={session.Client.Store.View.Turn} outcome={session.Client.Store.View.Outcome} state={replay.StateHash}");
                _battleScreen.GetNode<Button>("Toolbar/Restart").EmitSignal(BaseButton.SignalName.Pressed);
                await Until(() => _session is not null && _session != session && !_busy);
                if (_session!.Client.Store.View!.Stage != "Mulligan") { throw new InvalidOperationException("Restart failed."); }
                var restarted = _session;
                _battleScreen.GetNode<Button>("Toolbar/Back").EmitSignal(BaseButton.SignalName.Pressed);
                await Until(() => _session is null && !_busy);
                if (restarted.AiStatus!.State != "Stopped") { throw new InvalidOperationException("Returning to menu did not stop AI."); }
            }
            GD.Print("P95_GODOT_AI_SMOKE_OK difficulties=3 completed=3 replay=verified restart=verified cancel=verified");
            GetTree().Quit();

            async Task Until(Func<bool> condition)
            {
                while (!condition()) { deadline.Token.ThrowIfCancellationRequested(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            }
        }
        catch (Exception error) { _busy = false; GD.PushError(error.ToString()); await CloseSession(); GetTree().Quit(1); }
    }

    private async Task CaptureAi(string name)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (DisplayServer.GetName() == "headless") { return; }
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var path = ProjectSettings.GlobalizePath("res://artifacts/p95-ai-" + name + ".png");
        var result = GetViewport().GetTexture().GetImage().SavePng(path);
        if (result != Error.Ok) { throw new IOException("AI screenshot failed: " + result); }
    }
}
