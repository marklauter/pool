namespace Pool;

/// <summary>
/// pool
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IPool<T>
    where T : notnull
{
    /// <summary>
    /// clears the pool and sets allocated to zero
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken)

    /// <summary>
    /// simple lease.
    /// returns an item from the pool or creates a new item while the pool is not full.
    /// waits forever.
    /// </summary>
    /// <returns>item from the pool</returns>
    Task<T> LeaseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// lease with timeout. 
    /// returns an item from the pool or creates a new item while the pool is not full.
    /// waits forever.
    /// </summary>
    /// <param name="timeout">time to wait for available item</param>
    /// <returns>item from the pool</returns>
    Task<T> LeaseAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// returns an item to the pool
    /// </summary>
    /// <param name="item"></param>
    Task ReleaseAsync(T item, CancellationToken cancellationToken);

    /// <summary>
    /// returns the number of items currently allocated by the pool  
    /// </summary>
    int Allocated { get; }

    /// <summary>
    /// returns the number of unused, allocated items
    /// </summary>
    int Available { get; }

    /// <summary>
    /// returns the number of items currently leased
    /// </summary>
    int ActiveLeases { get; }

    /// <summary>
    /// returns the number of unsatisfied lease requests
    /// </summary>
    int Backlog { get; }
}
