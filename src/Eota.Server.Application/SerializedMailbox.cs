using System.Threading.Channels;

namespace Eota.Server.Application;

// Waiting writers observe backpressure; cancellation before admission never executes the request.
internal sealed class SerializedMailbox : IAsyncDisposable
{
    private readonly Channel<Func<Task>> _requests;
    private readonly Task _worker;
    private int _highWatermark;
    public int Queued => _requests.Reader.Count;
    public int HighWatermark => Volatile.Read(ref _highWatermark);

    public SerializedMailbox(int capacity)
    {
        _requests = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _worker = RunAsync();
    }

    public async Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _requests.Writer.WriteAsync(async () =>
        {
            try { result.TrySetResult(await action().ConfigureAwait(false)); }
            catch (Exception error) { result.TrySetException(error); }
        }, cancellationToken).ConfigureAwait(false);
        var count = _requests.Reader.Count;
        int previous;
        do { previous = _highWatermark; if (count <= previous) { break; } }
        while (Interlocked.CompareExchange(ref _highWatermark, count, previous) != previous);
        return await result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        await foreach (var request in _requests.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await request().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _requests.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }
}
