namespace Pool.DefaultStrategies;

internal sealed class DefaultKeepAliveStrategy<TPoolItem> : IKeepAliveStrategy<TPoolItem>
    where TPoolItem : class
{
    public ValueTask<bool> EnsureAliveAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);
}
