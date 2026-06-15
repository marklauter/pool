using Microsoft.Extensions.Logging;
using Pool.Metrics;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Pool;

/// <inheritdoc/>
[SuppressMessage("Naming", "CA1724:TypeNamesShouldNotMatchNamespaces",
    Justification = "Package, root namespace, and primary type are intentionally all named 'Pool' for API ergonomics (using Pool; new Pool<T>(...)). The type is generic, so it does not collide with the namespace in practice; renaming would break the published public API.")]
public sealed class Pool<TPoolItem>
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

    // capacity gate: a permit is the right to hold one item, so permits == free capacity and the
    // leased count can never exceed maxSize. the semaphore's own wait queue is the lease backlog,
    // which removes the separate waiter queue (and the lost-wakeup / cancel-vs-handoff races with it).
    private readonly SemaphoreSlim gate;

    // callers currently parked in WaitAsync. the semaphore hides its waiter count, so QueuedLeases
    // needs this explicit counter; only the contended (slow) path increments it.
    private int queuedLeases;

    private readonly int maxSize;
    private readonly int initialSize;
    private readonly bool isPreparationRequired;
    private readonly IItemFactory<TPoolItem> itemFactory;
    private readonly ILogger<Pool<TPoolItem>> logger;
    private readonly IPreparationStrategy<TPoolItem>? preparationStrategy;
    private readonly TimeSpan leaseTimeout;
    private readonly TimeSpan idleTimeout;
    private readonly TimeSpan preparationTimeout;
    private bool disposed;
    private readonly IPoolMetrics metrics;
    // handles for the observable-instrument registrations; disposed on pool disposal so the
    // instruments stop reporting and no longer root this pool via the (longer-lived) meter.
    private readonly IDisposable[] observerRegistrations;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// PoolName is the name of the pool in the form $"{typeof(TPoolItem).Name}.Pool"
    /// </summary>
    public static readonly string PoolName = $"{typeof(TPoolItem).Name}.Pool";

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="metrics"><see cref="IPoolMetrics"/></param>
    /// <param name="itemFactory"><see cref="IItemFactory{TPoolItem}"/></param>
    /// <param name="logger"></param>
    /// <param name="options"><see cref="PoolOptions"/></param>
    /// <param name="timeProvider">clock for idle-timeout tracking; defaults to <see cref="TimeProvider.System"/></param>
    public Pool(
        IItemFactory<TPoolItem> itemFactory,
        ILogger<Pool<TPoolItem>> logger,
        IPoolMetrics metrics,
        PoolOptions options,
        TimeProvider? timeProvider = null)
        : this(itemFactory, logger, metrics, null, options, timeProvider)
    { }

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="metrics"><see cref="IPoolMetrics"/></param>
    /// <param name="itemFactory"><see cref="IItemFactory{TPoolItem}"/></param>
    /// <param name="logger"></param>
    /// <param name="preparationStrategy"><see cref="IPreparationStrategy{TPoolItem}"/></param>
    /// <param name="options"><see cref="PoolOptions"/></param>
    /// <param name="timeProvider">clock for idle-timeout tracking; defaults to <see cref="TimeProvider.System"/></param>
    /// <exception cref="ArgumentNullException"></exception>
    public Pool(
        IItemFactory<TPoolItem> itemFactory,
        ILogger<Pool<TPoolItem>> logger,
        IPoolMetrics metrics,
        IPreparationStrategy<TPoolItem>? preparationStrategy,
        PoolOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(itemFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(options);
        // backstop for every construction path (direct new(...) bypasses the options pipeline);
        // also keeps SemaphoreSlim(maxSize, maxSize) from throwing a cryptic out-of-range error
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MinSize);

        this.itemFactory = itemFactory;
        this.logger = logger;
        this.metrics = metrics;
        this.timeProvider = timeProvider ?? TimeProvider.System;

        isPreparationRequired = preparationStrategy is not null;
        this.preparationStrategy = preparationStrategy;
        // PoolOptions carries its own defaults (MaxSize 100, timeouts InfiniteTimeSpan), so read directly
        maxSize = options.MaxSize;
        initialSize = Math.Min(options.MinSize, maxSize);

        leaseTimeout = options.LeaseTimeout;
        idleTimeout = options.IdleTimeout;
        preparationTimeout = options.PreparationTimeout;

        // seed the idle pool first so a factory failure mid-seed disposes what it already created
        // (I4) and leaves no SemaphoreSlim behind: the gate is built only once seeding succeeds
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

        // one permit per unit of capacity; the leased count can never exceed maxSize
        gate = new SemaphoreSlim(maxSize, maxSize);

        // counters are derived from gate + pool, so register the observers once both exist.
        // keep the registration handles so Dispose can unregister the instruments.
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
    /// <remarks>derived: leased (permits held) plus idle (available).</remarks>
    public int ItemsAllocated => ActiveLeases + ItemsAvailable;

    /// <inheritdoc/>
    public int ItemsAvailable => pool.Count;

    /// <inheritdoc/>
    /// <remarks>derived: permits held == capacity minus free permits.</remarks>
    public int ActiveLeases => maxSize - gate.CurrentCount;

    /// <inheritdoc/>
    public int QueuedLeases => Volatile.Read(ref queuedLeases);

    /// <inheritdoc/>
    public string Name => PoolName;

    /// <inheritdoc/>
    public ValueTask<TPoolItem> LeaseAsync() => LeaseAsync(CancellationToken.None);

    /// <inheritdoc/>
    public async ValueTask<TPoolItem> LeaseAsync(CancellationToken cancellationToken)
    {
        using var logscope = logger.BeginScope("{PoolName}.{Method}:{Id}", PoolName, nameof(LeaseAsync), Guid.NewGuid());

        var startTimestamp = timeProvider.GetTimestamp();
        try
        {
            ThrowIfDisposed();
            logger.LogDebug("starting lease request, pool state: {ItemsAllocated} of {MaxSize} allocated, {ItemsAvailable} available",
                ItemsAllocated, maxSize, ItemsAvailable);

            // fast path: take a permit without waiting (None is intentional — the non-blocking probe
            // never cancels; cancellation is honored in the slow WaitAsync below and in preparation).
            // only the contended path counts as a queued lease.
            if (!gate.Wait(0, CancellationToken.None))
            {
                logger.LogInformation("no items available, queuing lease request. lease queue size: {QueuedLeases}", QueuedLeases);
                _ = Interlocked.Increment(ref queuedLeases);
                try
                {
                    // drive the lease timeout off timeProvider so it is deterministically testable.
                    // SemaphoreSlim.WaitAsync has no TimeProvider overload, so a CTS(delay, timeProvider)
                    // supplies the timeout, linked with the caller's token into a single wait token.
                    using var timeoutCts = new CancellationTokenSource(leaseTimeout, timeProvider);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
                    try
                    {
                        await gate.WaitAsync(linkedCts.Token);
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        // preserve the v6 contract: a lease timeout surfaces as TaskCanceledException
                        throw new TaskCanceledException();
                    }
                }
                finally
                {
                    _ = Interlocked.Decrement(ref queuedLeases);
                }
            }

            // INVARIANT: a permit is held from here. every exit below either releases it exactly once
            // (failure -> inner catch) or transfers ownership to the caller (success -> a later Release(item)).
            try
            {
                var item = TryAcquireItem();
                // record the lease wait (acquire + queue wait) before preparation, so the metric
                // excludes prep time — which RecordPreparationTime already captures separately
                RecordLeaseWaitTime(startTimestamp);
                return await EnsurePreparedAsync(item, cancellationToken);
            }
            catch
            {
                // never leak the permit
                _ = gate.Release();
                throw;
            }
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
    public void Release(TPoolItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ThrowIfDisposed();
        using var logscope = logger.BeginScope("{PoolName}.{Method}:{Id}", PoolName, nameof(Release), Guid.NewGuid());

        // make the item available before returning the permit so a woken waiter always finds it
        pool.Enqueue(PoolItem.Create(timeProvider.GetUtcNow(), item));
        _ = gate.Release();
        logger.LogDebug("returned item to the pool");
    }

    /// <inheritdoc/>
    public void Clear()
    {
        using var logscope = logger.BeginScope("{PoolName}.{Method}:{Id}", PoolName, nameof(Clear), Guid.NewGuid());

        ThrowIfDisposed();

        // dispose every currently-idle item; items leased out are unaffected and re-enter on release
        DrainPoolAndDisposeItems();

        // refill idle up to MinSize, capped by free capacity so we never over-allocate past maxSize.
        // idle items hold no permit, so reseeding never touches the semaphore.
        var seed = Math.Min(initialSize, gate.CurrentCount);
        for (var i = 0; i < seed; i++)
        {
            pool.Enqueue(PoolItem.Create(timeProvider.GetUtcNow(), itemFactory.CreateItem()));
        }
    }

    private IEnumerable<TPoolItem> CreateItems(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return itemFactory.CreateItem();
        }
    }

    /// <remarks>a permit is held, so the caller is entitled to exactly one item: a valid idle one, or a fresh one.</remarks>
    private TPoolItem TryAcquireItem()
    {
        // one clock read drives the whole dequeue scan, so every candidate is judged against the same now
        var now = timeProvider.GetUtcNow();
        while (pool.TryDequeue(out var pooled))
        {
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
            // discard and dispose it rather than returning it to the pool, otherwise the same broken
            // item would poison the next leaser. the caller's permit is released by LeaseAsync, and
            // the caller is expected to retry the lease.
            DisposeItem(item);
            metrics.RecordPreparationException(ex);
            throw;
        }
    }

    private void DrainPoolAndDisposeItems()
    {
        while (pool.TryDequeue(out var item))
        {
            DisposeItem(item.Item);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, nameof(Pool<>));

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

        // any caller parked in WaitAsync wakes with ObjectDisposedException
        DrainPoolAndDisposeItems();
        gate.Dispose();
    }
}
