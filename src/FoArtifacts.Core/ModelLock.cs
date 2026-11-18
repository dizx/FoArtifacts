namespace FoArtifacts.Core;

/// <summary>
/// One writer or builder at a time: a batch, a build and an index refresh never overlap, so a build never
/// compiles a model while a batch is writing to it. The MCP server shares one instance.
/// </summary>
public sealed class ModelLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Releaser(_gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
