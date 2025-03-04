﻿using Pool.Metrics;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Pool;

/// <inheritdoc/>
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
    private readonly int maxSize;
    private readonly int initialSize;
    private readonly bool preparationRequired;
    private readonly IItemFactory<TPoolItem> itemFactory;
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
    /// <param name="options"><see cref="PoolOptions"/></param>
    public Pool(
        IPoolMetrics metrics,
        IItemFactory<TPoolItem> itemFactory,
        PoolOptions options)
        : this(metrics, itemFactory, null, options)
    { }

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="metrics"><see cref="IPoolMetrics"/></param>
    /// <param name="itemFactory"><see cref="IItemFactory{TPoolItem}"/></param>
    /// <param name="preparationStrategy"><see cref="IPreparationStrategy{TPoolItem}"/></param>
    /// <param name="options"><see cref="PoolOptions"/></param>
    /// <exception cref="ArgumentNullException"></exception>
    public Pool(
        IPoolMetrics metrics,
        IItemFactory<TPoolItem> itemFactory,
        IPreparationStrategy<TPoolItem>? preparationStrategy,
        PoolOptions options)
    {
        this.itemFactory = itemFactory ?? throw new ArgumentNullException(nameof(itemFactory));
        this.metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

        preparationRequired = preparationStrategy is not null;
        this.preparationStrategy = preparationStrategy;

        maxSize = options?.MaxSize ?? Int32.MaxValue;
        initialSize = Math.Min(options?.MinSize ?? 0, maxSize);

        leaseTimeout = options?.LeaseTimeout ?? Timeout.InfiniteTimeSpan;
        idleTimeout = options?.IdleTimeout ?? Timeout.InfiniteTimeSpan;
        preparationTimeout = options?.PreparationTimeout ?? Timeout.InfiniteTimeSpan;

        pool = new(CreateItems(initialSize).Select(PoolItem.Create));
    }

    /// <inheritdoc/>
    public int ItemsAllocated { get; private set; }

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
    public async ValueTask<TPoolItem> LeaseAsync(CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            if (ThrowIfDisposed().TryAcquireItem(out var item))
            {
                metrics.RecordLeaseWaitTime(timer.Elapsed);
                return await EnsurePreparedAsync(item.Item, cancellationToken);
            }

            var leaseRequest = new LeaseRequest(leaseTimeout, cancellationToken);
            leaseRequests.Enqueue(leaseRequest);

            var result = await leaseRequest.Task;
            metrics.RecordLeaseWaitTime(timer.Elapsed);
            return result;
        }
        catch (Exception)
        {
            metrics.RecordLeaseFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task ReleaseAsync(TPoolItem item) => ReleaseAsync(item, CancellationToken.None);

    /// <inheritdoc/>
    public async Task ReleaseAsync(
        TPoolItem item,
        CancellationToken cancellationToken)
    {
        if (ThrowIfDisposed().leaseRequests.IsEmpty)
        {
            // no active lease requests, so return the item to the pool
            pool.Enqueue(PoolItem.Create(item));
            return;
        }

        item = await EnsurePreparedAsync(item, cancellationToken);
        while (leaseRequests.TryDequeue(out var leaseRequest))
        {
            // Tries to set the task result and return, which unblocks the caller awaiting the lease request.
            // if TryTaskSetResult returns false,
            // then the lease request is timed out or canceled,
            // and the caller already knows about the cancelation,
            // so we can safely ignore the lease request.
            // This effectively purges dead requests from the queue.
            if (leaseRequest.TrySetResult(item))
            {
                return;
            }
        }

        // no valid lease requests, so return the item to the pool
        pool.Enqueue(PoolItem.Create(item));
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task ClearAsync() => ClearAsync(CancellationToken.None);

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed().EnsureItemsDisposed();

        await Task.WhenAll(
            CreateItems(QueuedLeases > initialSize ? QueuedLeases : initialSize)
            .Select(item => ReleaseAsync(item, cancellationToken)));
    }

    private IEnumerable<TPoolItem> CreateItems(int count)
    {
        lock (this)
        {
            ItemsAllocated = count;
        }

        for (var i = 0; i < count; i++)
        {
            yield return CreateItem(i + 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryAcquireItem(out PoolItem item) =>
        TryDequeue(out item) || TryCreateItem(out item);

    private bool TryCreateItem(out PoolItem item)
    {
        bool canCreate;
        int allocated;
        lock (this)
        {
            canCreate = ItemsAllocated < maxSize;
            if (!canCreate)
            {
                item = default;
                return false;
            }

            allocated = IncrmentItemsAllocated(false);
        }

        item = PoolItem.Create(CreateItem(allocated));
        return true;
    }

    private TPoolItem CreateItem(int allocated)
    {
        metrics.RecordItemCreated();
        metrics.RecordPoolUtilization(ActiveLeases, allocated);
        return itemFactory.CreateItem();
    }

    private bool TryDequeue(out PoolItem item)
    {
        while (pool.TryDequeue(out item))
        {
            if (IdleTimeIsNotExceeded(idleTimeout, item))
            {
                return true;
            }

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
        metrics.RecordPoolUtilization(ActiveLeases, DecrementItemsAllocated());
        if (IsPoolItemDisposable)
        {
            (item as IDisposable)?.Dispose();
            metrics.RecordItemDisposed();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int DecrementItemsAllocated()
    {
        lock (this)
        {
            return --ItemsAllocated;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int IncrmentItemsAllocated(bool withLock)
    {
        if (withLock)
        {
            lock (this)
            {
                return ++ItemsAllocated;
            }
        }

        return ++ItemsAllocated;
    }

    private async ValueTask<TPoolItem> EnsurePreparedAsync(
        TPoolItem item,
        CancellationToken cancellationToken)
    {
        if (!preparationRequired)
        {
            return item;
        }

        var timer = Stopwatch.StartNew();
        try
        {
            using var timeoutCts = new CancellationTokenSource(preparationTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            cancellationToken = linkedCts.Token;

            if (await preparationStrategy!.IsReadyAsync(item, cancellationToken))
            {
                return item;
            }

            await preparationStrategy.PrepareAsync(item, cancellationToken);

            metrics.RecordPreparationTime(timer.Elapsed);
            return item;
        }
        catch
        {
            metrics.RecordPreparationFailure();
            throw;
        }
    }

    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP007:Don't dispose injected", Justification = "it's all private to pool")]
    private void EnsureItemsDisposed()
    {
        if (!IsPoolItemDisposable)
        {
            return;
        }

        while (pool.TryDequeue(out var item))
        {
            (item.Item as IDisposable)?.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Pool<TPoolItem> ThrowIfDisposed() => disposed
        ? throw new ObjectDisposedException(nameof(Pool<TPoolItem>))
        : this;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        while (leaseRequests.TryDequeue(out var leaseRequest))
        {
            leaseRequest.Dispose();
        }

        EnsureItemsDisposed();

        disposed = true;
    }
}
