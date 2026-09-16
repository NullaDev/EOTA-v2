using System.Collections.Immutable;
using System.Text.Json;
using Eota.Client.Desktop;
using Eota.Transport.Contracts;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private async Task SmokeRemoteAsync()
    {
        try
        {
            var directory = ProjectSettings.GlobalizePath("res://artifacts/P9GodotReplay");
            var expected = DesktopReplay.Load(_catalog, Path.Combine(directory, "replay.json"), Audience.PlayerOne);
            await using var one = await DesktopSession.RemoteAsync(new Uri("ws://127.0.0.1:5086/matches/p9-smoke/ws?audience=playerOne"), "p9-smoke", _catalog.RuleHash);
            await using var two = await DesktopSession.RemoteAsync(new Uri("ws://127.0.0.1:5086/matches/p9-smoke/ws?audience=playerTwo"), "p9-smoke", _catalog.RuleHash);
            _session = one; BuildBattle();
            using var commands = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "commands.json")));
            foreach (var command in commands.RootElement.EnumerateArray())
            {
                _session = command.GetProperty("playerId").GetInt32() == 0 ? one : two;
                await _session.Client.SynchronizeAsync();
                var lane = command.GetProperty("laneId");
                ClientPayload payload = command.GetProperty("kind").GetString() switch
                {
                    "planCard" => new PlanCardPayload(command.GetProperty("cardInstanceId").GetUInt64(), lane.GetInt32()),
                    "planSpell" => new PlanSpellPayload(command.GetProperty("cardInstanceId").GetUInt64(), lane.ValueKind == JsonValueKind.Null ? null : lane.GetInt32()),
                    "cancelPlan" => new CancelPlanPayload(command.GetProperty("planOrdinal").GetUInt64()),
                    "submitMulligan" => new SubmitMulliganPayload(command.GetProperty("replacedCards").EnumerateArray().Select(value => value.GetUInt64()).ToImmutableArray()),
                    "submitTurn" => new SubmitTurnPayload(),
                    _ => throw new InvalidDataException("Unsupported smoke command.")
                };
                var ack = await _session.SubmitAsync(payload);
                if (!ack.Accepted) { throw new InvalidOperationException(ack.Code); }
            }
            _session = one; await one.Client.SynchronizeAsync(); BuildBattle();
            if (ObserverViewHasher.Compute(expected.Final) != one.Client.Store.ObserverViewHash)
            { throw new InvalidOperationException("Remote projection differs from verified local replay."); }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            GD.Print($"P9_GODOT_REMOTE_SMOKE_OK turn={expected.Final.Turn} replayState={expected.StateHash} view={one.Client.Store.ObserverViewHash}");
            _session = null; _battle = false; GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); _session = null; _battle = false; GetTree().Quit(1); }
    }
}
