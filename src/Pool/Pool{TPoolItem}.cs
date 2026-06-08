using Microsoft.Extensions.Logging;
using Pool.Metrics;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Pool;

/// <inheritdoc/>
[SuppressMessage("Naming", "CA1724:TypeNamesShouldNotMatchNamespaces",
    Justification = "Package, root namespace, and primary type are intentionally all named 'Pool' for API ergonomics (using Pool; new Pool<T>(...)). The type is generic, so it does not collide with the namespace in practice; renaming would break the published public API.")]
public sealed class Pool<TPoolItem>
    : IPool<TPoolItem>
    where TPoolItem : class
{
    /// <summary>
    /// LeaseRequest allows async leasing of pool items. 
    /// It's essentially a wrapper around a task completion source.
    /// </summary>
    private sealed class LeaseRequest
        : IDisposable
    {
        public Task<TPoolItem> Task => taskCompletionSource.Task;

        private readonly TaskCompletionSource<TPoolItem> taskCompletionSource = new();
        private readonly CancellationTokenSource? timeoutTokenSource;
        private readonly CancellationTokenSource? linkedTokenSource;
        private readonly CancellationTokenRegistration? cancellationTokenRegistration;
        private bool disposed;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsNotExpired() => !Task.IsCompleted && !Task.IsCompletedSuccessfully;

        public LeaseRequest(TimeSpan timeout, CancellationToken cancellationToken)
        {
            // when timeout is infinite and cancellation token is none, don't register any callbacks
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                // when timeout is infinite and cancellation token is not none, register cancellation token canceled callback
                if (cancellationToken != CancellationToken.None)
                {
                    cancellationTokenRegistration = cancellationToken.Register(Cancel);
                }

                return;
            }

            timeoutTokenSource = new CancellationTokenSource(timeout);

            // when cancellation token is none, only register timeout token canceled callback
            if (cancellationToken == CancellationToken.None)
            {
                cancellationTokenRegistration = timeoutTokenSource.Token.Register(Cancel);
                return;
            }

            // when both timeout and cancellation token are provided, register linked token canceled callback
            linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutTokenSource.Token);

            cancellationTokenRegistration = linkedTokenSource.Token.Register(Cancel);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TrySetResult(TPoolItem item)
        {
            if (taskCompletionSource.TrySetResult(item))
            {
                Dispose();
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Cancel()
        {
            // if taskCompletionSource.Task.IsCompletedSuccessfully, then dispose is already called
            // if taskCompletionSource.TrySetCanceled() returns false, then it's already canceled, faulted, or completed (ran to completion)
            if (!taskCompletionSource.Task.IsCompletedSuccessfully
                && taskCompletionSource.TrySetCanceled())
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            if (timeoutTokenSource is not null)
            {
                // 1.  Canceling unblocks any awaiters.
                // 2.  Cancel() will either...
                //      2.a  call SetCanceled() directly if there is no linked token source,
                //      2.b  or it will trigger Cancel() on the linked token source,
                // 3.  so the linked token source does not need to be canceled explicitly.
                timeoutTokenSource.Cancel();
                timeoutTokenSource.Dispose();
            }

            linkedTokenSource?.Dispose();

            cancellationTokenRegistration?.Dispose();

            Cancel();
        }
    }

    private readonly record struct PoolItem(
        DateTime IdleSince,
        TPoolItem Item)
    {
        public static PoolItem Create(TPoolItem item) => new(DateTime.UtcNow, item);
        public TimeSpan IdleTime => DateTime.UtcNow - IdleSince;
    }

    private static readonly bool IsPoolItemDisposable = typeof(TPoolItem).GetInterface(nameof(IDisposable), true) is not null;

    private readonly ConcurrentQueue<PoolItem> pool;
    private readonly ConcurrentQueue<LeaseRequest> leaseRequests = new();
    // guards itemsAllocated
    private readonly Lock gate = new();
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
    public Pool(
        IItemFactory<TPoolItem> itemFactory,
        ILogger<Pool<TPoolItem>> logger,
        IPoolMetrics metrics,
        PoolOptions options)
        : this(itemFactory, logger, metrics, null, options)
    { }

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="metrics"><see cref="IPoolMetrics"/></param>
    /// <param name="itemFactory"><see cref="IItemFactory{TPoolItem}"/></param>
    /// <param name="logger"></param>
    /// <param name="preparationStrategy"><see cref="IPreparationStrategy{TPoolItem}"/></param>
    /// <param name="options"><see cref="PoolOptions"/></param>
    /// <exception cref="ArgumentNullException"></exception>
    [SuppressMessage("Style", "IDE0306:Simplify collection initialization", Justification = "no it can't")]
    public Pool(
        IItemFactory<TPoolItem> itemFactory,
        ILogger<Pool<TPoolItem>> logger,
        IPoolMetrics metrics,
        IPreparationStrategy<TPoolItem>? preparationStrategy,
        PoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(itemFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(metrics);

        this.itemFactory = itemFactory;
        this.logger = logger;
        this.metrics = metrics;

        this.metrics.RegisterItemsAllocatedObserver(() => ItemsAllocated);
        this.metrics.RegisterItemsAvailableObserver(() => ItemsAvailable);
        this.metrics.RegisterActiveLeasesObserver(() => ActiveLeases);
        this.metrics.RegisterQueuedLeasesObserver(() => QueuedLeases);
        this.metrics.RegisterUtilizationRateObserver(() => ItemsAllocated == 0 ? 0 : (double)ActiveLeases / ItemsAllocated);

        isPreparationRequired = preparationStrategy is not null;
        this.preparationStrategy = preparationStrategy;
        maxSize = options?.MaxSize ?? int.MaxValue;
        initialSize = Math.Min(options?.MinSize ?? 0, maxSize);

        leaseTimeout = options?.LeaseTimeout ?? Timeout.InfiniteTimeSpan;
        idleTimeout = options?.IdleTimeout ?? Timeout.InfiniteTimeSpan;
        preparationTimeout = options?.PreparationTimeout ?? Timeout.InfiniteTimeSpan;

        pool = new(CreateItems(initialSize).Select(PoolItem.Create));

        logger.LogInformation("{PoolName} created with {@Options}", PoolName, options);
    }

    private volatile int itemsAllocated;
    /// <inheritdoc/>
    public int ItemsAllocated => itemsAllocated;

    /// <inheritdoc/>
    public int ItemsAvailable => pool?.Count ?? 0;

    /// <inheritdoc/>
    public int ActiveLeases => ItemsAllocated - ItemsAvailable;

    /// <inheritdoc/>
    public int QueuedLeases => leaseRequests.Count;

    /// <inheritdoc/>
    public string Name => PoolName;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<TPoolItem> LeaseAsync() => LeaseAsync(CancellationToken.None);

    /// <inheritdoc/>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "the LeaseRequest is handed to the leaseRequests queue; it is disposed via TrySetResult/Cancel when fulfilled, or by Pool.Dispose — not within this scope")]
    public async ValueTask<TPoolItem> LeaseAsync(CancellationToken cancellationToken)
    {
        using var logscope = logger.BeginScope("{PoolName}.{Method}:{Id}", PoolName, nameof(LeaseAsync), Guid.NewGuid());

        var timer = Stopwatch.StartNew();
        try
        {
            logger.LogDebug("starting lease request, pool state: {ItemsAllocated} of {MaxSize} allocated, {ItemsAvailable} available",
                ItemsAllocated, maxSize, ItemsAvailable);

            if (IsNotDisposed().TryAcquireItem(out var item))
            {
                RecordLeaseWaitTime(timer);
                // returns item to queue on preparation failure
                return await EnsurePreparedAsync(item.Item, cancellationToken);
            }

            logger.LogInformation("no items available, queuing lease request. lease queue size: {QueuedLeases}", QueuedLeases);
            // lease requests are pulled off the queue in the Release method
            var leaseRequest = new LeaseRequest(leaseTimeout, cancellationToken);
            leaseRequests.Enqueue(leaseRequest);

            // wait for a released item to be handed to this request, then record the true queue wait
            var leased = await leaseRequest.Task;
            RecordLeaseWaitTime(timer);
            // returns item to queue on preparation failure
            return await EnsurePreparedAsync(leased, cancellationToken);
        }
        catch (Exception ex)
        {
            RecordLeaseException(timer, ex);
            throw;
        }
    }

    private void RecordLeaseException(Stopwatch timer, Exception ex)
    {
        timer.Stop();
        logger.LogError(ex, "lease failure after {ElapsedMs}ms", timer.ElapsedMilliseconds);
        metrics.RecordLeaseException(ex);
    }

    private void RecordLeaseWaitTime(Stopwatch timer)
    {
        timer.Stop();
        logger.LogDebug("lease completed in {ElapsedMs}ms", timer.ElapsedMilliseconds);
        metrics.RecordLeaseWaitTime(timer.Elapsed);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "the dequeued LeaseRequest is disposed via TrySetResult/Cancel when fulfilled or purged as expired, or by Pool.Dispose; ownership stays with the queue, not this scope")]
    public void Release(TPoolItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _ = IsNotDisposed();
        using var logscope = logger.BeginScope("{PoolName}.{Method}:{Id}", PoolName, nameof(Release), Guid.NewGuid());

        logger.LogDebug("{PendingLeaseRequestCount}", leaseRequests.Count);
        while (leaseRequests.TryDequeue(out var leaseRequest))
        {
            // Tries to set the task result and return, which unblocks the caller awaiting the lease request.
            // if TryTaskSetResult returns false,
            // then the lease request is timed out or canceled,
            // and the caller already knows about the cancelation,
            // so we can safely ignore the lease request.
            // This purges expired requests from the queue.
            if (leaseRequest.IsNotExpired() && leaseRequest.TrySetResult(item))
            {
                logger.LogDebug("fulfilled queued lease request");
                return;
            }
        }

        // there are no valid lease requests, so return the item to the pool
        logger.LogDebug("returned item to the pool");
        pool.Enqueue(PoolItem.Create(item));
    }

    /// <inheritdoc/>
    public void Clear()
    {
        using var logscope = logger.BeginScope("{PoolName}.{Method}:{Id}", PoolName, nameof(Clear), Guid.NewGuid());

        // drain the idle queue (pool)
        IsNotDisposed().DrainPoolAndDisposeItems();
        var count = QueuedLeases > initialSize ? QueuedLeases : initialSize;

        // create a batch of warm items to fill the pool 
        // and fulfill queued lease requests 
        var items = CreateItems(count);

        // push items onto the pool
        // and fullfill queued lease requests.
        foreach (var item in items)
        {
            Release(item);
        }
    }

    private List<TPoolItem> CreateItems(int count)
    {
        lock (gate)
        {
            itemsAllocated = count;
        }

        var items = new List<TPoolItem>(count);
        for (var i = 0; i < count; i++)
        {
            items.Add(itemFactory.CreateItem());
        }

        return items;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryAcquireItem(out PoolItem item)
    {
        if (TryDequeue(out item))
        {
            logger.LogDebug("TryAcquireItem {AcquisitionMethod}", "dequeued");
            return true;
        }

        if (TryCreateItem(out item))
        {
            logger.LogDebug("TryAcquireItem {AcquisitionMethod}", "created");
            return true;
        }

        return false;
    }

    private bool TryCreateItem(out PoolItem item)
    {
        bool canCreate;
        lock (gate)
        {
            canCreate = itemsAllocated < maxSize;
            if (!canCreate)
            {
                item = default;
                logger.LogWarning("{PoolName}.{MethodName} maximum capacity reached {MaxCapacity}", PoolName, nameof(TryCreateItem), maxSize);
                return false;
            }

            ++itemsAllocated;
        }

        logger.LogDebug("{PoolName}.{MethodName} creating new pool item, total allocated: {ItemsAllocated}", PoolName, nameof(TryCreateItem), itemsAllocated);
        try
        {
            item = PoolItem.Create(itemFactory.CreateItem());
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{PoolName}.{MethodName} failure", PoolName, nameof(TryCreateItem));
            lock (gate)
            {
                --itemsAllocated;
            }

            throw;
        }
    }

    private bool TryDequeue(out PoolItem item)
    {
        while (pool.TryDequeue(out item))
        {
            if (IdleTimeIsNotExceeded(idleTimeout, item))
            {
                return true;
            }

            logger.LogInformation("{PoolName}.{MethodName} removing expired item, {IdleTimeMs}ms exceeded limit: {IdleTimeoutMs}ms",
                PoolName, nameof(TryDequeue), item.IdleTime.TotalMilliseconds, idleTimeout.TotalMilliseconds);
            RemoveItem(item.Item);
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IdleTimeIsNotExceeded(TimeSpan idleTimeout, PoolItem item) =>
        idleTimeout == Timeout.InfiniteTimeSpan || item.IdleTime < idleTimeout;

    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP007:Don't dispose injected", Justification = "it's all private to pool")]
    private void RemoveItem(TPoolItem item)
    {
        lock (gate)
        {
            --itemsAllocated;
        }

        logger.LogDebug("{PoolName}.{MethodName} removing item from pool, total allocated: {ItemsAllocated}", PoolName, nameof(RemoveItem), itemsAllocated);
        if (IsPoolItemDisposable)
        {
            (item as IDisposable)?.Dispose();
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
            var timer = Stopwatch.StartNew();
            using var timeoutCts = new CancellationTokenSource(preparationTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            cancellationToken = linkedCts.Token;

            if (await preparationStrategy!.IsReadyAsync(item, cancellationToken))
            {
                logger.LogDebug("item already prepared, skipping preparation step");
                return item;
            }

            logger.LogDebug("preparing item with timeout: {PreparationTimeoutMs}ms", preparationTimeout.TotalMilliseconds);
            await preparationStrategy.PrepareAsync(item, cancellationToken);

            timer.Stop();
            metrics.RecordPreparationTime(timer.Elapsed);
            logger.LogDebug("item preparation completed in {ElapsedMs}ms", timer.ElapsedMilliseconds);
            return item;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "preparation failure");
            // preparation failed, so the item is in an indeterminate state (e.g. a dropped socket).
            // discard and dispose it rather than returning it to the pool, otherwise the same broken
            // item would poison the next leaser. the caller is expected to retry the lease.
            RemoveItem(item);
            metrics.RecordPreparationException(ex);
            throw;
        }
    }

    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP007:Don't dispose injected", Justification = "it's all private to pool")]
    private void DrainPoolAndDisposeItems()
    {
        while (pool.TryDequeue(out var item))
        {
            if (IsPoolItemDisposable)
            {
                (item.Item as IDisposable)?.Dispose();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Pool<TPoolItem> IsNotDisposed() => disposed
        ? throw new ObjectDisposedException(nameof(Pool<>))
        : this;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        while (leaseRequests.TryDequeue(out var leaseRequest))
        {
            leaseRequest.Dispose();
        }

        DrainPoolAndDisposeItems();
    }
}
