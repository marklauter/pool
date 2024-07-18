namespace Pool;

/// <summary>
/// IKeepAliveStrategy is an interface for keeping idle pool items alive.
/// </summary>
/// <typeparam name="TPoolItem"></typeparam>
public interface IKeepAliveStrategy<TPoolItem>
    where TPoolItem : class
{
    /// <summary>
    /// EnsureAliveAsync checks if the pool item is alive and keeps it alive.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>ValueTask{bool}</returns>
    ValueTask<bool> EnsureAliveAsync(CancellationToken cancellationToken);
}
