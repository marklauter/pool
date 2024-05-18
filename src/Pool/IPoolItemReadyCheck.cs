namespace Pool;

/// <summary>
/// IPoolItemReadyCheck for checking if a pool item is ready, and making it ready if it is not.
/// </summary>
/// <typeparam name="TPoolItem"></typeparam>
public interface IPoolItemReadyCheck<TPoolItem>
    where TPoolItem : notnull
{
    /// <summary>
    /// IsReadyAsync checks if the pool item is ready before the pool leases it to the caller.
    /// </summary>
    /// <param name="item"></param>
    /// <param name="cancellationToken"></param>
    /// <returns><see cref="Boolean"/> true if the pool item is ready.</returns>
    ValueTask<bool> IsReadyAsync(TPoolItem item, CancellationToken cancellationToken);

    /// <summary>
    /// MakeReadyAsync makes the pool item ready before the pool leases it to the caller.
    /// </summary>
    /// <param name="item"></param>
    /// <param name="cancellationToken"></param>
    /// <returns><see cref="Task"/></returns>
    /// <remarks>The pool will call MakeReadyAsync when IsReadyAsync returns false. Implement MakeReadyAsync to initialize an object, or establish a connection, like connecting to a database or smtp server.</remarks>
    Task MakeReadyAsync(TPoolItem item, CancellationToken cancellationToken);
}
