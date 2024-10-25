namespace Pool.DefaultStrategies;

internal sealed class DefaultPreparationStrategy<TKey, TPool> : IPreparationStrategy<TKey, TPool>
    where TKey : class
    where TPool : class
{
    public ValueTask<bool> IsReadyAsync(TKey connectionKey, TPool item, CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public Task PrepareAsync(TKey connectionKey, TPool item, CancellationToken cancellationToken) => Task.CompletedTask;
}
