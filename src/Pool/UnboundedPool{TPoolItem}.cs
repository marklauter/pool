using Microsoft.Extensions.Logging;
using Pool.Metrics;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Pool;

/// <summary>
/// An unbounded pool modeled on <see cref="System.Buffers.ArrayPool{T}"/>: <see cref="LeaseAsync()"/>
/// never waits — it returns an idle item or creates one on demand — and <see cref="Release(TPoolItem)"/>
/// is an optional optimization that donates an item back for reuse. A lease transfers ownership of the
/// item to the caller; if the caller never releases it, the item is simply not reused (and, if it is
/// <see cref="IDisposable"/>, disposing it becomes the caller's responsibility, exactly as for any
/// object the caller owns). The pool only ever disposes the idle items it still holds — on a capped
/// return that overflows, on idle-timeout eviction, on <see cref="Clear"/>, and on <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// Contrast with <see cref="Pool{TPoolItem}"/>, which is a <em>borrow</em>: the bounded pool retains
/// title and a lease must be returned. Here a lease is an <em>ownership transfer</em>, which is what
/// makes optional release safe even for disposable items.
/// </remarks>
/// <typeparam name="TPoolItem">the reference type managed by the pool.</typeparam>
public sealed class UnboundedPool<TPoolItem>
    : IPool<TPoolItem>
    where TPoolItem : class
{
    private readonly record struct PoolItem(
        DateTimeOffset IdleSince,
        TPoolItem Item)
    {
        // the clock read is the caller's: the pool passes timeProvider.GetUtcNow() so the struct
        // stays clock-free and idle expiry is deterministically testable (a FakeTimeProvider drives it)
        public static PoolItem Create(DateTimeOffset now, TPoolItem item) => new(now, item);
    }

    private static readonly bool IsPoolItemDisposable = typeof(TPoolItem).GetInterface(nameof(IDisposable), true) is not null;

    // idle (available) items waiting to be leased
    private readonly ConcurrentQueue<PoolItem> pool;

    // the retention accountant: reserved before enqueue and released on dequeue/drop, so the MaxIdle
    // cap is enforced without reading ConcurrentQueue.Count on the Release hot path. It may briefly
    // skew from pool.Count inside an enqueue/dequeue, so it gates the cap; pool.Count reports availability.
    private int idleCount;

    // outstanding leases: handed-out minus returned. an item a caller leased and never released stays
    // counted (it genuinely is still out there), so this is an honest gauge for an unbounded pool.
    private int activeLeases;

    private readonly int maxIdle;
    private readonly int initialSize;
    private readonly bool isPreparationRequired;
    private readonly IItemFactory<TPoolItem> itemFactory;
    private readonly ILogger<UnboundedPool<TPoolItem>> logger;
    private readonly IPreparationStrategy<TPoolItem>? preparationStrategy;
    private readonly TimeSpan idleTimeout;
    private readonly TimeSpan preparationTimeout;
    private bool disposed;
    private readonly IPoolMetrics metrics;
    // handles for the observable-instrument registrations; disposed on pool disposal so the
    // instruments stop reporting and no longer root this pool via the (longer-lived) meter.
    private readonly IDisposable[] observerRegistrations;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// PoolName is the name of the pool in the form $"{typeof(TPoolItem).Name}.UnboundedPool"
    /// </summary>
    public static readonly string PoolName = $"{typeof(TPoolItem).Name}.UnboundedPool";

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="itemFactory"><see cref="IItemFactory{TPoolItem}"/></param>
    /// <param name="logger"></param>
    /// <param name="metrics"><see cref="IPoolMetrics"/></param>
    /// <param name="options"><see cref="UnboundedPoolOptions"/></param>
    /// <param name="timeProvider">clock for idle-timeout tracking; defaults to <see cref="TimeProvider.System"/></param>
    public UnboundedPool(
        IItemFactory<TPoolItem> itemFactory,
        ILogger<UnboundedPool<TPoolItem>> logger,
        IPoolMetrics metrics,
        UnboundedPoolOptions options,
        TimeProvider? timeProvider = null)
        : this(itemFactory, logger, metrics, null, options, timeProvider)
    { }

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="itemFactory"><see cref="IItemFactory{TPoolItem}"/></param>
    /// <param name="logger"></param>
    /// <param name="metrics"><see cref="IPoolMetrics"/></param>
    /// <param name="preparationStrategy"><see cref="IPreparationStrategy{TPoolItem}"/></param>
    /// <param name="options"><see cref="UnboundedPoolOptions"/></param>
    /// <param name="timeProvider">clock for idle-timeout tracking; defaults to <see cref="TimeProvider.System"/></param>
    /// <exception cref="ArgumentNullException"></exception>
    public UnboundedPool(
        IItemFactory<TPoolItem> itemFactory,
        ILogger<UnboundedPool<TPoolItem>> logger,
        IPoolMetrics metrics,
        IPreparationStrategy<TPoolItem>? preparationStrategy,
        UnboundedPoolOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(itemFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(options);
        // backstop for every construction path (direct new(...) bypasses the options pipeline)
        ArgumentOutOfRangeException.ThrowIfNegative(options.MinSize);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxIdle);

        this.itemFactory = itemFactory;
        this.logger = logger;
        this.metrics = metrics;
        this.timeProvider = timeProvider ?? TimeProvider.System;

        isPreparationRequired = preparationStrategy is not null;
        this.preparationStrategy = preparationStrategy;
        maxIdle = options.MaxIdle;
        // never seed past the retention cap, otherwise the pool would start over its own idle limit
        initialSize = Math.Min(options.MinSize, maxIdle);

        idleTimeout = options.IdleTimeout;
        preparationTimeout = options.PreparationTimeout;

        // seed the idle pool first so a factory failure mid-seed disposes what it already created
        // and leaves the pool with consistent state
        pool = new();
        try
        {
            foreach (var item in CreateItems(initialSize))
            {
                pool.Enqueue(PoolItem.Create(this.timeProvider.GetUtcNow(), item));
            }
        }
        catch
        {
            DrainPoolAndDisposeItems();
            throw;
        }

        idleCount = pool.Count;

        observerRegistrations =
        [
            this.metrics.RegisterItemsAllocatedObserver(() => ItemsAllocated),
            this.metrics.RegisterItemsAvailableObserver(() => ItemsAvailable),
            this.metrics.RegisterActiveLeasesObserver(() => ActiveLeases),
            this.metrics.RegisterQueuedLeasesObserver(() => QueuedLeases),
            this.metrics.RegisterUtilizationRateObserver(() => ItemsAllocated == 0 ? 0 : (double)ActiveLeases / ItemsAllocated),
        ];

        logger.LogInformation("{PoolName} created with {@Options}", PoolName, options);
    }

    /// <inheritdoc/>
    /// <remarks>derived: outstanding leases plus idle (available).</remarks>
    public int ItemsAllocated => ActiveLeases + ItemsAvailable;

    /// <inheritdoc/>
    public int ItemsAvailable => pool.Count;

    /// <inheritdoc/>
    /// <remarks>outstanding leases: items handed out and not yet returned, including any a caller dropped without releasing.</remarks>
    public int ActiveLeases => Volatile.Read(ref activeLeases);

    /// <inheritdoc/>
    /// <remarks>always zero: an unbounded lease never waits, so nothing is ever queued.</remarks>
    public int QueuedLeases => 0;

    /// <inheritdoc/>
    public string Name => PoolName;

    /// <inheritdoc/>
    public ValueTask<TPoolItem> LeaseAsync() => LeaseAsync(CancellationToken.None);

    /// <inheritdoc/>
    /// <remarks>never waits for capacity: returns an idle item or creates one. cancellation is honored up front and during preparation.</remarks>
    public async ValueTask<TPoolItem> LeaseAsync(CancellationToken cancellationToken)
    {
        using var logscope = logger.BeginScope("{PoolName}.{Method}:{Id}", PoolName, nameof(LeaseAsync), Guid.NewGuid());

        var startTimestamp = timeProvider.GetTimestamp();
        try
        {
            ThrowIfDisposed();
            // honor an already-cancelled token before acquiring, so there is nothing to unwind
            cancellationToken.ThrowIfCancellationRequested();

            logger.LogDebug("starting lease request, pool state: {ItemsAllocated} allocated, {ItemsAvailable} available",
                ItemsAllocated, ItemsAvailable);

            // no gate: an item is always available immediately (idle or freshly created)
            var item = TryAcquireItem();
            // record the lease wait (acquire only — never a queue wait here) before preparation, so the
            // metric excludes prep time, which RecordPreparationTime already captures separately
            RecordLeaseWaitTime(startTimestamp);
            var prepared = await EnsurePreparedAsync(item, cancellationToken);

            // count the lease only once it is successfully handed to the caller. a preparation failure
            // disposed the item inside EnsurePreparedAsync and never reached here, so activeLeases stays
            // correct without a compensating decrement.
            _ = Interlocked.Increment(ref activeLeases);
            return prepared;
        }
        catch (Exception ex)
        {
            RecordLeaseException(startTimestamp, ex);
            throw;
        }
    }

    private void RecordLeaseException(long startTimestamp, Exception ex)
    {
        var elapsed = timeProvider.GetElapsedTime(startTimestamp);
        logger.LogError(ex, "lease failure after {ElapsedMs}ms", elapsed.TotalMilliseconds);
        metrics.RecordLeaseException(ex);
    }

    private void RecordLeaseWaitTime(long startTimestamp)
    {
        var elapsed = timeProvider.GetElapsedTime(startTimestamp);
        logger.LogDebug("lease completed in {ElapsedMs}ms", elapsed.TotalMilliseconds);
        metrics.RecordLeaseWaitTime(elapsed);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// optional: the caller owns the item once leased. releasing donates it back for reuse if the pool
    /// is under its idle cap; otherwise the item is dropped (and disposed, if disposable).
    /// </remarks>
    public void Release(TPoolItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ThrowIfDisposed();
        using var logscope = logger.BeginScope("{PoolName}.{Method}:{Id}", PoolName, nameof(Release), Guid.NewGuid());

        // the caller handed the item back, so it is no longer outstanding regardless of whether we retain it
        _ = Interlocked.Decrement(ref activeLeases);

        // optimistically reserve an idle slot; if that would exceed the cap, undo and drop the item.
        // reserving before the enqueue keeps the cap honest under concurrent returns (no overshoot).
        if (Interlocked.Increment(ref idleCount) <= maxIdle)
        {
            pool.Enqueue(PoolItem.Create(timeProvider.GetUtcNow(), item));
            logger.LogDebug("returned item to the pool");
            return;
        }

        _ = Interlocked.Decrement(ref idleCount);
        logger.LogDebug("idle cap {MaxIdle} reached, dropping returned item", maxIdle);
        DisposeItem(item);
    }

    /// <inheritdoc/>
    public void Clear()
    {
        using var logscope = logger.BeginScope("{PoolName}.{Method}:{Id}", PoolName, nameof(Clear), Guid.NewGuid());

        ThrowIfDisposed();

        // dispose every currently-idle item; items leased out are unaffected (the caller owns them)
        DrainPoolAndDisposeItems();

        // refill idle up to MinSize (already capped by MaxIdle in initialSize)
        for (var i = 0; i < initialSize; i++)
        {
            pool.Enqueue(PoolItem.Create(timeProvider.GetUtcNow(), itemFactory.CreateItem()));
        }

        _ = Interlocked.Exchange(ref idleCount, pool.Count);
    }

    private IEnumerable<TPoolItem> CreateItems(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return itemFactory.CreateItem();
        }
    }

    /// <remarks>returns a valid idle item if one exists, dropping any that have exceeded the idle timeout, otherwise creates a fresh one.</remarks>
    private TPoolItem TryAcquireItem()
    {
        // one clock read drives the whole dequeue scan, so every candidate is judged against the same now
        var now = timeProvider.GetUtcNow();
        while (pool.TryDequeue(out var pooled))
        {
            _ = Interlocked.Decrement(ref idleCount);
            if (IdleTimeIsNotExceeded(idleTimeout, pooled, now))
            {
                logger.LogDebug("TryAcquireItem {AcquisitionMethod}", "dequeued");
                return pooled.Item;
            }

            logger.LogInformation("{PoolName}.{MethodName} removing expired item, {IdleTimeMs}ms exceeded limit: {IdleTimeoutMs}ms",
                PoolName, nameof(TryAcquireItem), (now - pooled.IdleSince).TotalMilliseconds, idleTimeout.TotalMilliseconds);
            DisposeItem(pooled.Item);
        }

        logger.LogDebug("TryAcquireItem {AcquisitionMethod}", "created");
        return itemFactory.CreateItem();
    }

    private static bool IdleTimeIsNotExceeded(TimeSpan idleTimeout, PoolItem item, DateTimeOffset now) =>
        idleTimeout == Timeout.InfiniteTimeSpan || now - item.IdleSince < idleTimeout;

    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP007:Don't dispose injected", Justification = "it's all private to pool")]
    private static void DisposeItem(TPoolItem item)
    {
        if (IsPoolItemDisposable)
        {
            // IsPoolItemDisposable guarantees TPoolItem implements IDisposable, so the cast is safe
            ((IDisposable)item).Dispose();
        }
    }

    /// <remarks>
    /// removes and disposes the pool item if preparation fails
    /// </remarks>
    private async ValueTask<TPoolItem> EnsurePreparedAsync(
        TPoolItem item,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("{PoolName}.{MethodName} ensuring item preparation, required: {IsPreparationRequired}",
            PoolName, nameof(EnsurePreparedAsync), isPreparationRequired);

        if (!isPreparationRequired)
        {
            return item;
        }

        try
        {
            var startTimestamp = timeProvider.GetTimestamp();
            using var timeoutCts = new CancellationTokenSource(preparationTimeout, timeProvider);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            cancellationToken = linkedCts.Token;

            if (await preparationStrategy!.IsReadyAsync(item, cancellationToken))
            {
                logger.LogDebug("item already prepared, skipping preparation step");
                return item;
            }

            logger.LogDebug("preparing item with timeout: {PreparationTimeoutMs}ms", preparationTimeout.TotalMilliseconds);
            await preparationStrategy.PrepareAsync(item, cancellationToken);

            var elapsed = timeProvider.GetElapsedTime(startTimestamp);
            metrics.RecordPreparationTime(elapsed);
            logger.LogDebug("item preparation completed in {ElapsedMs}ms", elapsed.TotalMilliseconds);
            return item;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "preparation failure");
            // preparation failed, so the item is in an indeterminate state (e.g. a dropped socket).
            // discard and dispose it rather than handing it to the caller. the lease was never counted
            // (activeLeases is incremented only on success), and the caller is expected to retry.
            DisposeItem(item);
            metrics.RecordPreparationException(ex);
            throw;
        }
    }

    private void DrainPoolAndDisposeItems()
    {
        while (pool.TryDequeue(out var item))
        {
            _ = Interlocked.Decrement(ref idleCount);
            DisposeItem(item.Item);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, nameof(UnboundedPool<>));

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        // sever the observable instruments first so a concurrent metrics collection cannot read
        // pool state mid-teardown, and so the meter no longer roots this disposed pool
        foreach (var registration in observerRegistrations)
        {
            registration?.Dispose();
        }

        // dispose the idle items the pool still owns; leased-out items belong to their callers
        DrainPoolAndDisposeItems();
    }
}
