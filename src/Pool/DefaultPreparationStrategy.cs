namespace Pool;

internal sealed class DefaultPreparationStrategy<TPoolItem> : IPreparationStrategy<TPoolItem>
    where TPoolItem : class
{
    public ValueTask<bool> IsReadyAsync(TPoolItem item, CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public Task PrepareAsync(TPoolItem item, CancellationToken cancellationToken) => Task.CompletedTask;
}
