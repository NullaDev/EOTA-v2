using System.Diagnostics;
using Eota.Transport.Contracts;

namespace Eota.Client.Core;

public sealed record MatchTimerReading(MatchTimerPayload Payload, long ReceivedTimestamp)
{
    // Estimate elapsed time monotonically; a client's wall-clock setting cannot extend the server deadline.
    public int RemainingSeconds => (int)Math.Clamp(Math.Ceiling(((double)Payload.DeadlineUnixMilliseconds - Payload.ServerNowUnixMilliseconds) / 1000
        - Stopwatch.GetElapsedTime(ReceivedTimestamp).TotalSeconds), 0, 3600);
}
