namespace Pool;

/// <summary>
/// A Pool of TPoolItem
/// </summary>
/// <typeparam name="TPoolItem"></typeparam>
public interface IPool<TPoolItem>
    : IDisposable
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
    /// simple lease.
    /// returns an item from the pool or creates a new item while the pool is not full.
    /// waits forever.
    /// </summary>
    /// <returns>item from the pool</returns>
    ValueTask<TPoolItem> LeaseAsync();

    /// <summary>
    /// simple lease.
    /// returns an item from the pool or creates a new item while the pool is not full.
    /// waits forever.
    /// </summary>
    /// <returns>item from the pool</returns>
    ValueTask<TPoolItem> LeaseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// returns an item to the pool
    /// </summary>
    /// <param name="item"></param>
    Task ReleaseAsync(TPoolItem item);

    /// <summary>
    /// returns an item to the pool
    /// </summary>
    /// <param name="item"></param>
    /// <param name="cancellationToken"></param>
    Task ReleaseAsync(TPoolItem item, CancellationToken cancellationToken);

    /// <summary>
    /// returns how many items are currently allocated by the pool
    /// </summary>
    int ItemsAllocated { get; }

    /// <summary>
    /// returns the how many items are of allocated but not leased
    /// </summary>
    int ItemsAvailable { get; }

    /// <summary>
    /// returns how many items are currently leased
    /// </summary>
    int ActiveLeases { get; }

    /// <summary>
    /// returns how many lease requests are awaiting fulfillment
    /// </summary>
    int QueuedLeases { get; }
}
