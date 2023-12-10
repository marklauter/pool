namespace Pool;

public interface IPool<T> where T : notnull
{
    /// <summary>
    /// simple lease.
    /// returns an item from the pool or creates a new item while the pool is not full.
    /// waits forever.
    /// </summary>
    /// <returns>item from the pool</returns>
    Task<T> LeaseAsync();

    /// <summary>
    /// lease with timeout. 
    /// returns an item from the pool or creates a new item while the pool is not full.
    /// waits forever.
    /// </summary>
    /// <returns>item from the pool</returns>
    Task<T> LeaseAsync(TimeSpan timeout);

    /// <summary>
    /// lease with item validation. 
    /// returns an item from the pool or creates a new item.
    /// tests the item for readiness through the isReady function.
    /// for example: make sure a database connection is still open.
    /// </summary>
    /// <param name="isReady">tests the item for readiness</param>
    /// <param name="timeout">time to wait for available item</param>
    /// <returns>item from the pool</returns>
    Task<T> LeaseAsync(TimeSpan timeout, Func<T, bool> isReady);

    /// <summary>
    /// returns an item to the pool
    /// </summary>
    /// <param name="item"></param>
    void Release(T item);

    /// <summary>
    /// returns the number of items currently allocated by the pool  
    /// </summary>
    long Size { get; }
}
