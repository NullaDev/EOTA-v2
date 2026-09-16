using Eota.Kernel.Commands;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Transport.Contracts;

namespace Eota.Server.Application;

public sealed record MatchClockState(int Turn, MatchStage Stage, bool Active);

public sealed partial class MatchActor
{
    private readonly TimeProvider _time;
    private MatchTimerPayload? _timer;

    public Task<MatchClockState> GetClockStateAsync(CancellationToken token = default) =>
        RunAsync(() => new MatchClockState(_state.Turn, _state.Stage, !_faulted && _state.Status == MatchStatus.Active), token);

    public Task FaultAsync(Exception error, CancellationToken token = default) => RunAsync(() =>
    {
        _faulted = true;
        _faultCode = error.GetType().Name;
        foreach (var observer in _connections) { observer.Close(new IOException("match-faulted", error)); }
        return true;
    }, token);

    // Trusted server scheduling only. There is deliberately no client SystemTimeout payload.
    public Task SetClockAsync(MatchTimerPayload timer, CancellationToken token = default) => RunAsync(() =>
    {
        if (timer.Turn != _state.Turn || timer.Stage != _state.Stage.ToString() || _faulted) { return false; }
        _timer = timer;
        foreach (var observer in _connections) { SendTimer(observer); }
        return true;
    }, token);

    public Task ExpireClockAsync(CancellationToken token = default) => RunAsync(() =>
    {
        try { ExpireClock(); }
        catch (Exception error)
        {
            _faulted = true;
            foreach (var observer in _connections) { observer.Close(new IOException("match-faulted", error)); }
            throw;
        }
        return true;
    }, token);

    private void ExpireClock()
    {
        if (_faulted || _state.Status != MatchStatus.Active || _timer is not { } timer
            || timer.Turn != _state.Turn || timer.Stage != _state.Stage.ToString()
            || _time.GetUtcNow().ToUnixTimeMilliseconds() < timer.DeadlineUnixMilliseconds) { return; }
        foreach (var seat in new[] { PlayerId.One, PlayerId.Two })
        {
            if (_state.Turn != timer.Turn || _state.Stage.ToString() != timer.Stage) { break; }
            var player = _state.Players.Single(p => p.Id == seat);
            if (_state.Stage == MatchStage.Mulligan ? player.Mulligan is not { Submitted: false }
                : _state.Stage != MatchStage.Planning || player.TurnSubmitted) { continue; }
            var stage = _state.Stage;
            MatchRecoveryValidator.Validate(_state);
            var command = new SystemTimeoutCommand(seat, player.CommandRevision, _state.Turn, stage);
            var transition = MatchCommandProcessor.Accept(_state, command);
            if (!transition.IsAccepted) { throw new InvalidOperationException("system-timeout-rejected"); }
            var envelope = new ClientEnvelope(ContractJson.Version, MatchId, $"system-timeout-{timer.Turn}-{timer.Stage}-{seat.Value}",
                0, player.CommandRevision, stage == MatchStage.Mulligan ? new SubmitMulliganPayload([]) : new SubmitTurnPayload());
            CompleteTransition(transition, seat == PlayerId.One ? Audience.PlayerOne : Audience.PlayerTwo, envelope,
                timeout: new StoredSystemTimeout(timer.Turn, timer.Stage));
        }
    }

    private void SendTimer(MatchConnection observer)
    {
        if (!observer.Closed && _state.Status == MatchStatus.Active && _timer is { } timer
            && timer.Turn == _state.Turn && timer.Stage == _state.Stage.ToString())
        { observer.Publish(timer with { ServerNowUnixMilliseconds = _time.GetUtcNow().ToUnixTimeMilliseconds() }, _state.Revision); }
    }
}
