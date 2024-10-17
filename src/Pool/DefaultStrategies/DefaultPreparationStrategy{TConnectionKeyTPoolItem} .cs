namespace Pool.DefaultStrategies;

internal sealed class DefaultPreparationStrategy<TConnectionKey, TPoolItem> : IPreparationStrategy<TConnectionKey, TPoolItem>
    where TConnectionKey : class
    where TPoolItem : class
{
    public ValueTask<bool> IsReadyAsync(TConnectionKey connectionKey, TPoolItem item, CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public Task PrepareAsync(TConnectionKey connectionKey, TPoolItem item, CancellationToken cancellationToken) => Task.CompletedTask;
}
