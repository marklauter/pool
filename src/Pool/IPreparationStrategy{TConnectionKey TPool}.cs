namespace Pool;
/// <summary>
/// IPreparationStrategy is an interface for preparing pool pools before the pool leases them to the caller.
/// </summary>
/// <typeparam name="TKey"></typeparam>
/// <typeparam name="TPool"></typeparam>
public interface IPreparationStrategy<TKey, TPool>
    where TKey : class
    where TPool : class
{
    /// <summary>
    /// IsReadyAsync checks if the pool item is ready before the pool leases it to the caller.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="pool"></param>
    /// <param name="cancellationToken"></param>
    /// <returns><see cref="Boolean"/> true if the pool item is ready.</returns>
    ValueTask<bool> IsReadyAsync(TKey key, TPool pool, CancellationToken cancellationToken);

    /// <summary>
    /// PrepareAsync makes the pool item ready before the pool leases it to the caller.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="pool"></param>
    /// <param name="cancellationToken"></param>
    /// <returns><see cref="Task"/></returns>
    /// <remarks>The pool will call PrepareAsync when IsReadyAsync returns false. Implement PrepareAsync to initialize an object, or establish a connection, like connecting to a database or smtp server.</remarks>
    Task PrepareAsync(TKey key, TPool pool, CancellationToken cancellationToken);
}
