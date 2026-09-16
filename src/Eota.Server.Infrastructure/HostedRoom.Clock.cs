using System.Text.Json;
using Eota.Kernel.Matches;
using Eota.Server.Application;
using Eota.Transport.Contracts;

namespace Eota.Server.Infrastructure;

public sealed partial class HostedRoom
{
    private readonly TimeProvider _time;
    private readonly RoomTiming _timing;
    private readonly string _settingsHash;
    private RoomDeadline? _deadline;
    private bool _clockLoaded;

    public async Task TickAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Actor is null || !_timing.IsLimited) { return; }
            var state = await Actor.GetClockStateAsync(token).ConfigureAwait(false);
            if (!state.Active) { return; }
            await UpdateClockAsync(state, token).ConfigureAwait(false);
            await Actor.ExpireClockAsync(token).ConfigureAwait(false);
            var next = await Actor.GetClockStateAsync(token).ConfigureAwait(false);
            if (next.Active && next != state) { await UpdateClockAsync(next, token).ConfigureAwait(false); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (Actor is not null) { await Actor.FaultAsync(error, token).ConfigureAwait(false); }
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task UpdateClockAsync(MatchClockState state, CancellationToken token)
    {
        var limit = state.Stage switch { MatchStage.Mulligan => _timing.MulliganSeconds, MatchStage.Planning => _timing.PlanningSeconds, _ => 0 };
        if (limit == 0) { return; }
        var path = Path.Combine(_directory, "deadline.json");
        if (!_clockLoaded)
        {
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 4096) { throw new InvalidDataException("invalid-room-deadline"); }
                _deadline = JsonSerializer.Deserialize<RoomDeadline>(File.ReadAllText(path)) ?? throw new InvalidDataException("invalid-room-deadline");
                if (_deadline.SettingsHash != _settingsHash || _deadline.DueUnixMilliseconds < 0)
                { throw new InvalidDataException("room-deadline-mismatch"); }
            }
            _clockLoaded = true;
        }
        if (_deadline is { } previous && previous.Turn == state.Turn && previous.Stage == state.Stage && previous.LimitSeconds == limit)
        {
            // Installing again is safe, but avoid publishing a timer frame on every scheduler tick.
            if (_installed == previous) { return; }
        }
        else
        {
            _deadline = new RoomDeadline(state.Turn, state.Stage, _time.GetUtcNow().AddSeconds(limit).ToUnixTimeMilliseconds(), limit, _settingsHash);
            AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(_deadline));
        }
        await Actor!.SetClockAsync(new MatchTimerPayload(state.Turn, state.Stage.ToString(), _time.GetUtcNow().ToUnixTimeMilliseconds(),
            _deadline.DueUnixMilliseconds, limit), token).ConfigureAwait(false);
        _installed = _deadline;
    }

    private RoomDeadline? _installed;
    private sealed record RoomDeadline(int Turn, MatchStage Stage, long DueUnixMilliseconds, int LimitSeconds, string SettingsHash);
}
