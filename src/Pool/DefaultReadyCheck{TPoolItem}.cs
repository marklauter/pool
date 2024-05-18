namespace Pool;

internal sealed class DefaultReadyCheck<TPoolItem> : IReadyCheck<TPoolItem>
    where TPoolItem : notnull
{
    public Task<bool> IsReadyAsync(TPoolItem item, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task MakeReadyAsync(TPoolItem item, CancellationToken cancellationToken) => Task.CompletedTask;
}
