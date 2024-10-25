namespace Pool;

/// <summary>
/// pool
/// </summary>
/// <typeparam name="TKey"></typeparam>
/// <typeparam name="TPool"></typeparam>
public interface IPoolMap<TKey, TPool>
    where TKey : class
    where TPool : class
{
    /// <summary>
    /// clears the pool and sets allocated to zero
    /// </summary>
    Task ClearAsync();

    /// <summary>
    /// clears the pool and sets allocated to zero
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Simple lease.
    /// Check the connection pool, to see if there is an existing entry for that connection key, if there is one, it retrieves an item from the pool
    /// identified by that connection, if there is no entry for that key it will create a new one and retrieve the item from the pool this is while there is capacity available 
    /// for that pool, if there is no pools available for that request it  waits forever.
    /// waits forever.
    /// </summary>
    /// <returns>item from the pool</returns>
    ValueTask<TPool> LeaseAsync(TKey key);

    /// <summary>
    /// Simple lease.
    /// Check the connection pool, to see if there is an existing entry for that connection key, if there is one, it retrieves an item from the pool
    /// identified by that connection, if there is no entry for that key it will create a new one and retrieve the item from the pool this is while there is capacity available 
    /// for that pool, if there is no pools available for that request it  waits forever.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>item from the pool</returns>
    ValueTask<TPool> LeaseAsync(TKey key, CancellationToken cancellationToken);

    /// <summary>
    /// returns a pool identfied by the key
    /// </summary>
    /// <param name="key"></param>
    /// <param name="pool"></param>
    Task ReleaseAsync(TKey key, TPool pool);

    /// <summary>
    /// returns an item to the pool that is identified by the key
    /// </summary>
    /// <param name="key"></param>
    /// <param name="pool"></param>
    /// <param name="cancellationToken"></param>
    Task ReleaseAsync(TKey key, TPool pool, CancellationToken cancellationToken);

    /// <summary>
    /// returns how many pools are currently leased
    /// </summary>
    int UniqueLeases { get; }
}
