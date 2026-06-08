namespace Pool;

/// <summary>
/// A pool of reusable <typeparamref name="TPoolItem"/> instances. Hands items out via
/// <see cref="LeaseAsync()"/> and takes them back via <see cref="Release(TPoolItem)"/>,
/// creating items on demand up to a configured maximum and reusing idle ones.
/// </summary>
/// <typeparam name="TPoolItem">the reference type managed by the pool.</typeparam>
public interface IPool<TPoolItem>
    : IDisposable
    where TPoolItem : class
{
    /// <summary>
    /// disposes all currently-idle items, then refills the pool with fresh items —
    /// at least MinSize, and enough to fulfill any queued lease requests — handing
    /// those items to waiting requests. items currently leased out are unaffected
    /// and re-enter the pool when released.
    /// </summary>
    /// <exception cref="ObjectDisposedException">the pool has been disposed.</exception>
    void Clear();

    /// <summary>
    /// leases an item using <see cref="CancellationToken.None"/>. returns an idle item, or
    /// creates one if the pool is below its maximum size; otherwise waits for an item to be
    /// released — up to the configured lease timeout, which is infinite by default (waits
    /// indefinitely).
    /// </summary>
    /// <returns>a leased item from the pool.</returns>
    /// <exception cref="ObjectDisposedException">the pool has been disposed.</exception>
    ValueTask<TPoolItem> LeaseAsync();

    /// <summary>
    /// leases an item. returns an idle item, or creates one if the pool is below its maximum
    /// size; otherwise waits for an item to be released until the configured lease timeout
    /// elapses or <paramref name="cancellationToken"/> is canceled.
    /// </summary>
    /// <param name="cancellationToken">cancels the wait for an item.</param>
    /// <returns>a leased item from the pool.</returns>
    /// <exception cref="ObjectDisposedException">the pool has been disposed.</exception>
    /// <exception cref="OperationCanceledException">the lease timed out or <paramref name="cancellationToken"/> was canceled.</exception>
    ValueTask<TPoolItem> LeaseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// the name of the pool, in the form $"{typeof(TPoolItem).Name}.Pool".
    /// </summary>
    string Name { get; }

    /// <summary>
    /// returns a leased item to the pool. if a lease request is waiting, the item is handed
    /// to it directly; otherwise it becomes idle and available for the next lease.
    /// </summary>
    /// <param name="item">an item previously obtained from <see cref="LeaseAsync()"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">the pool has been disposed.</exception>
    void Release(TPoolItem item);

    /// <summary>
    /// how many items the pool currently owns — leased plus idle (available).
    /// </summary>
    int ItemsAllocated { get; }

    /// <summary>
    /// how many allocated items are idle (available) and not currently leased.
    /// </summary>
    int ItemsAvailable { get; }

    /// <summary>
    /// how many items are currently leased out (<see cref="ItemsAllocated"/> minus <see cref="ItemsAvailable"/>).
    /// </summary>
    int ActiveLeases { get; }

    /// <summary>
    /// how many lease requests are queued, awaiting an item to be released.
    /// </summary>
    int QueuedLeases { get; }
}
