namespace Pool;

internal sealed class DefaultReadyCheck<TPoolItem> : IPoolItemReadyCheck<TPoolItem>
    where TPoolItem : notnull
{
    public ValueTask<bool> IsReadyAsync(TPoolItem item, CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public Task MakeReadyAsync(TPoolItem item, CancellationToken cancellationToken) => Task.CompletedTask;
}
