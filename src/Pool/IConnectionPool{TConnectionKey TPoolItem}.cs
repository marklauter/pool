namespace Pool;

/// <summary>
/// pool
/// </summary>
/// <typeparam name="TConnectionKey"></typeparam>
/// <typeparam name="TPoolItem"></typeparam>
public interface IConnectionPool<TConnectionKey, TPoolItem>
    where TConnectionKey : class
    where TPoolItem : class
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
    /// identified by that connection, if there is no entry for that connectionKey it will create a new one and retrieve the item from the pool this is while there is capacity available 
    /// for that pool, if there is no items available for that request it  waits forever.
    /// waits forever.
    /// </summary>
    /// <returns>item from the pool</returns>
    ValueTask<TPoolItem> LeaseAsync(TConnectionKey connectionKey);

    /// <summary>
    /// Simple lease.
    /// Check the connection pool, to see if there is an existing entry for that connection key, if there is one, it retrieves an item from the pool
    /// identified by that connection, if there is no entry for that connectionKey it will create a new one and retrieve the item from the pool this is while there is capacity available 
    /// for that pool, if there is no items available for that request it  waits forever.
    /// </summary>
    /// <param name="connectionKey"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>item from the pool</returns>
    ValueTask<TPoolItem> LeaseAsync(TConnectionKey connectionKey, CancellationToken cancellationToken);

    /// <summary>
    /// returns an item to the pool that is identified by the connectionKey
    /// </summary>
    /// <param name="connectionKey"></param>
    /// <param name="item"></param>
    Task ReleaseAsync(TConnectionKey connectionKey, TPoolItem item);

    /// <summary>
    /// returns an item to the pool that is identified by the connectionKey
    /// </summary>
    /// <param name="connectionKey"></param>
    /// <param name="item"></param>
    /// <param name="cancellationToken"></param>
    Task ReleaseAsync(TConnectionKey connectionKey, TPoolItem item, CancellationToken cancellationToken);

    /// <summary>
    /// returns how many items are currently allocated by the pool
    /// </summary>
    int ItemsAvailable { get; }

    /// <summary>
    /// returns how many items are currently leased
    /// </summary>
    int UniqueLeases { get; }
}
