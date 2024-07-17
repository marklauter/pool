namespace Pool;

/// <summary>
/// IPreparationStrategy is an interface for preparing pool items before the pool leases them to the caller.
/// </summary>
/// <typeparam name="TPoolItem"></typeparam>
public interface IPreparationStrategy<TPoolItem>
    where TPoolItem : class
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
    Task PrepareAsync(TPoolItem item, CancellationToken cancellationToken);
}
