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
    void Clear();

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
    /// lease with item validation. 
    /// returns an item from the pool or creates a new item.
    /// tests the item for readiness through the isReady function.
    /// for example: make sure a database connection is still open.
    /// </summary>
    /// <param name="timeout">time to wait for available item</param>
    /// <param name="isReady">tests the item for readiness</param>
    /// <param name="makeReady">makes the item ready if ready check fails</param>
    /// <returns>item from the pool</returns>
    Task<T> LeaseAsync(
        TimeSpan timeout,
        Func<T, CancellationToken, Task<bool>> isReady,
        Func<T, CancellationToken, Task> makeReady,
        CancellationToken cancellationToken);

    /// <summary>
    /// returns an item to the pool
    /// </summary>
    /// <param name="item"></param>
    void Release(T item);

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
