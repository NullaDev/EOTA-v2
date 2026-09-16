using System.Collections.Concurrent;
using Eota.Kernel.Matches;
using Eota.Server.Application;

namespace Eota.Server.Infrastructure;

public sealed class InMemoryMatchDirectory : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, MatchActor> _matches = new(StringComparer.Ordinal);

    public async Task<MatchActor> CreateAsync(string matchId, MatchCreationRequest request)
    {
        var actor = new MatchActor(matchId, request);
        if (!_matches.TryAdd(matchId, actor))
        {
            await actor.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("match-already-exists");
        }

        return actor;
    }

    public MatchActor? Find(string matchId) => _matches.GetValueOrDefault(matchId);

    public async ValueTask DisposeAsync()
    {
        foreach (var actor in _matches.Values) { await actor.DisposeAsync().ConfigureAwait(false); }
        _matches.Clear();
    }
}
