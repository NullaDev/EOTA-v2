using Eota.Client.Core;
using Eota.Transport.Contracts;

namespace Eota.Client.AI;

public sealed record AiPlayerStatus(string State, int AcceptedCommands = 0, int RejectedCommands = 0,
    int Evaluations = 0, string? Error = null);

// Owns a task, not the client connection. The session must stop this task before disposing clients.
public sealed class AiPlayer : IAsyncDisposable
{
    public const int MaximumCommandsPerPhase = 64;
    public const int MaximumConsecutiveRejections = 3;
    private readonly CancellationTokenSource _lifetime = new();
    private volatile AiPlayerStatus _status = new("Starting");
    private int _disposed;
    public AiPlayerStatus Status => _status;
    public Task Completion { get; }

    public AiPlayer(GameClient client, AiPolicy policy, TimeSpan? actionDelay = null)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(policy);
        var delay = actionDelay ?? TimeSpan.FromMilliseconds(160);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        Completion = Task.Run(() => RunAsync(client, policy, delay, _lifetime.Token));
    }

    private async Task RunAsync(GameClient client, AiPolicy policy, TimeSpan delay, CancellationToken token)
    {
        try
        {
            (int Turn, string Stage)? phase = null;
            var commands = 0; var rejected = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                client.Store.DrainPresentationFrames();
                var state = client.Store.State;
                if (state.NeedsSnapshot || state.View is null)
                {
                    _status = _status with { State = "Synchronizing" };
                    await client.SynchronizeAsync(token).ConfigureAwait(false);
                    continue;
                }
                var view = state.View;
                if (view.Status != "Active") { _status = _status with { State = "Finished" }; return; }
                var own = view.Private ?? throw new InvalidOperationException("ai-requires-player-seat");
                if (view.Players.Single(player => player.PlayerId == own.PlayerId).Submitted || view.Stage is not ("Planning" or "Mulligan"))
                {
                    _status = _status with { State = "Waiting" };
                    await client.Store.WaitForChangeAsync(state, token).ConfigureAwait(false);
                    continue;
                }
                if (phase != (view.Turn, view.Stage)) { phase = (view.Turn, view.Stage); commands = 0; rejected = 0; }
                _status = _status with { State = view.Stage == "Mulligan" ? "Mulligan" : "Thinking" };
                if (delay > TimeSpan.Zero) { await Task.Delay(delay, token).ConfigureAwait(false); }
                // A snapshot is a mailbox barrier: never plan from an intermediate resolution frame.
                await client.SynchronizeAsync(token).ConfigureAwait(false);
                view = client.Store.View!;
                if (phase != (view.Turn, view.Stage) || view.Status != "Active" || view.Players.Single(player => player.PlayerId == own.PlayerId).Submitted)
                { continue; }
                var decision = commands >= MaximumCommandsPerPhase - 1 && view.Stage == "Planning"
                    ? new AiDecision(new SubmitTurnPayload(), 0) : policy.Decide(view, token);
                if (decision.Command is null) { continue; }
                if (commands >= MaximumCommandsPerPhase) { throw new InvalidOperationException("ai-command-budget-exceeded"); }
                token.ThrowIfCancellationRequested();
                var ack = await client.SubmitAsync(decision.Command, view.Private!.CommandRevision, cancellationToken: token).ConfigureAwait(false);
                commands++;
                _status = _status with
                {
                    AcceptedCommands = _status.AcceptedCommands + (ack.Accepted ? 1 : 0),
                    RejectedCommands = _status.RejectedCommands + (ack.Accepted ? 0 : 1),
                    Evaluations = decision.Evaluations
                };
                rejected = ack.Accepted ? 0 : rejected + 1;
                if (rejected >= MaximumConsecutiveRejections) { throw new InvalidOperationException("ai-repeated-rejection: " + ack.Code); }
                // ACK can precede the final observer view. Always resync before issuing another action.
                await client.SynchronizeAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { _status = _status with { State = "Stopped" }; }
        catch (Exception error) { _status = _status with { State = "Faulted", Error = error.Message }; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await Completion.ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
