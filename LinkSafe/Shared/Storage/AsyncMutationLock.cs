namespace Shared.Storage;

public sealed class AsyncMutationLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        return new Lease(_gate);
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
