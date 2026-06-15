using Pool.Metrics;
using System.Diagnostics.CodeAnalysis;

namespace Pool;

/// <summary>
/// A scoped lease over a pooled <typeparamref name="TPoolItem"/>. Disposing the lease returns the
/// item to its owning pool exactly once; the pool retains title and remains responsible for disposing
/// the item itself. Use inside a <c>using</c> so the return happens on scope exit:
/// <code>
/// using var lease = await pool.LeaseScopeAsync(cancellationToken);
/// // use lease.Item
/// </code>
/// A lease that is garbage-collected without being disposed is counted as a leak on the
/// <c>pool.leases.leaked</c> counter (published under <see cref="PoolMeter.Name"/>).
/// </summary>
/// <remarks>
/// A lease is single-owner: it is not designed for concurrent use and disposal across threads. The
/// return to the pool is exactly-once even under a dispose race (it is interlocked), but the
/// <see cref="Item"/> use-after-return guard is best-effort and may not observe a concurrent dispose.
/// </remarks>
/// <typeparam name="TPoolItem">the reference type managed by the pool.</typeparam>
public sealed class Lease<TPoolItem>
    : IDisposable
    where TPoolItem : class
{
    private readonly IPool<TPoolItem> pool;
    // captured up front so the finalizer never has to touch the pool, which may be disposed by then
    private readonly string poolName;
    private TPoolItem? item;

    internal Lease(IPool<TPoolItem> pool, TPoolItem item)
    {
        this.pool = pool;
        poolName = pool.Name;
        this.item = item;
    }

    /// <summary>
    /// The leased item. Throws <see cref="ObjectDisposedException"/> once the lease has been disposed
    /// (returned), guarding against use-after-return.
    /// </summary>
    public TPoolItem Item => item ?? throw new ObjectDisposedException(nameof(Lease<TPoolItem>));

    /// <summary>
    /// Returns the item to the pool. Idempotent — a second dispose is a no-op, so a lease can never
    /// double-release. If the pool was disposed while the item was out on lease, the item is disposed
    /// here instead (the pool only reclaims idle items).
    /// </summary>
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP007:Don't dispose injected", Justification = "only reached when the pool is already disposed and can no longer reclaim the item; per the pool contract the orphaned leased item becomes the caller's to dispose")]
    public void Dispose()
    {
        // entering Dispose means this lease is not a leak, so suppress the finalizer first — before the
        // single-shot return below, which may throw. keep this call first: an unexpected Release failure
        // (e.g. a SemaphoreFullException from a gate corrupted elsewhere) then still propagates, but can
        // never skip suppression and be miscounted as a leak, which would corrupt the signal this feature
        // provides.
        GC.SuppressFinalize(this);

        // single-shot return: the item goes back exactly once even under double-dispose or a race,
        // which is what makes the lease immune to the double-release footgun
        var leased = Interlocked.Exchange(ref item, null);
        if (leased is null)
        {
            return;
        }

        try
        {
            pool.Release(leased);
        }
        catch (ObjectDisposedException)
        {
            // the pool was disposed while the item was out on lease — nothing to return it to.
            // the pool only disposes idle items, so this one is now ours to dispose (footgun #3)
            (leased as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Finalizer: runs only when <see cref="Dispose"/> was never called — i.e. the lease was leaked.
    /// Records the leak on the <c>pool.leases.leaked</c> counter so the footgun is observable. It
    /// touches only an immutable string and a static counter, so it is safe in a finalizer, and it
    /// deliberately does not return the item — resurrecting a possibly-broken item from the finalizer
    /// thread is unsafe — so the permit stays leaked, but is now counted.
    /// </summary>
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP023:Don't use reference types in finalizer context", Justification = "records only an immutable string field and a static counter; no instance reference is dereferenced, which is safe in a finalizer")]
    ~Lease() => LeaseLeakMetric.Record(poolName);
}
