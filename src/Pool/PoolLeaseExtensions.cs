namespace Pool;

/// <summary>
/// Additive RAII helpers over <see cref="IPool{TPoolItem}"/>. They build on the existing public
/// <see cref="IPool{TPoolItem}.LeaseAsync()"/> and <see cref="IPool{TPoolItem}.Release(TPoolItem)"/>,
/// so they require no changes to the pool and work with any implementation.
/// </summary>
public static class PoolLeaseExtensions
{
    /// <summary>
    /// Leases an item and wraps it in a <see cref="Lease{TPoolItem}"/> whose disposal returns it to
    /// the pool, so a <c>using</c> guarantees the return:
    /// <code>
    /// using var lease = await pool.LeaseScopeAsync(cancellationToken);
    /// // use lease.Item
    /// </code>
    /// </summary>
    /// <typeparam name="TPoolItem">the reference type managed by the pool.</typeparam>
    /// <param name="pool">the pool to lease from.</param>
    /// <param name="cancellationToken">cancels the wait for an item.</param>
    /// <returns>a disposable lease over a pooled item.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">the pool has been disposed.</exception>
    /// <exception cref="OperationCanceledException">the lease timed out or <paramref name="cancellationToken"/> was canceled.</exception>
    public static async ValueTask<Lease<TPoolItem>> LeaseScopeAsync<TPoolItem>(
        this IPool<TPoolItem> pool,
        CancellationToken cancellationToken = default)
        where TPoolItem : class
    {
        ArgumentNullException.ThrowIfNull(pool);

        var item = await pool.LeaseAsync(cancellationToken);
        // the ctor only assigns fields, so nothing throws between acquiring the item and wrapping it —
        // the permit cannot leak in the gap
        return new Lease<TPoolItem>(pool, item);
    }
}
