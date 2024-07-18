using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Pool;

/// <inheritdoc/>>
public sealed class Pool<TPoolItem>
    : IPool<TPoolItem>
    , IDisposable
    where TPoolItem : class
{
    private sealed class LeaseRequest
        : IDisposable
    {
        public Task<TPoolItem> Task => taskCompletionSource.Task;

        private readonly TaskCompletionSource<TPoolItem> taskCompletionSource = new();
        private readonly CancellationTokenSource? timeoutTokenSource;
        private readonly CancellationTokenSource? linkedTokenSource;
        private bool disposed;

        public LeaseRequest(TimeSpan timeout, CancellationToken cancellationToken)
        {
            // no timeout, so register cancellation token only
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                _ = cancellationToken.Register(TaskSetCanceled);
                return;
            }

            timeoutTokenSource = new CancellationTokenSource(timeout);
            // no cancellation token, so register timeout only
            if (cancellationToken == CancellationToken.None)
            {
                _ = timeoutTokenSource.Token.Register(TaskSetCanceled);
                return;
            }

            // both timeout and cancellation token, so register linked token source
            linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutTokenSource.Token);

            _ = linkedTokenSource.Token.Register(TaskSetCanceled);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TaskSetResult(TPoolItem item)
        {
            if (taskCompletionSource.TrySetResult(item))
            {
                Dispose();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TaskSetCanceled()
        {
            if (!taskCompletionSource.Task.IsCompletedSuccessfully
                && taskCompletionSource.TrySetCanceled())
            {
                Dispose();
            }
        }

        private void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            if (disposing)
            {
                if (timeoutTokenSource is not null)
                {
                    // Canceling unblocks any awaiters.
                    // Cancel() will either call SetCanceled() directly if there is no linked token source,
                    // or it will trigger Cancel() on the linked token source,
                    // so the linked token source will not need to be canceled explicitly.
                    timeoutTokenSource.Cancel();
                    timeoutTokenSource.Dispose();
                }

                linkedTokenSource?.Dispose();
            }
        }

        public void Dispose() => Dispose(disposing: true);
    }

    private record struct PoolItem(DateTime IdleSince, TPoolItem? Item)
    {
        public static PoolItem Create(TPoolItem item) => new(DateTime.UtcNow, item);
    }

    private static readonly bool IsPoolItemDisposable = typeof(TPoolItem).GetInterface(nameof(IDisposable), true) is not null;

    private readonly ConcurrentQueue<PoolItem> items;
    private readonly ConcurrentQueue<LeaseRequest> requests = new();
    private readonly int maxSize;
    private readonly int initialSize;
    private readonly bool preparationRequired;
    private readonly IItemFactory<TPoolItem> itemFactory;
    private readonly IPreparationStrategy<TPoolItem>? preparationStrategy;
    private readonly TimeSpan leaseTimeout;
    private readonly TimeSpan idleTimeout;
    private readonly TimeSpan preparationTimeout;
    private bool disposed;

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="itemFactory"></param>
    /// <param name="options"></param>
    public Pool(
        IItemFactory<TPoolItem> itemFactory,
        PoolOptions options)
        : this(itemFactory, null, options)
    { }

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="itemFactory"><see cref="IItemFactory{TPoolItem}"/></param>
    /// <param name="preparationStrategy"><see cref="IPreparationStrategy{TPoolItem}"/></param>
    /// <param name="options"><see cref="PoolOptions"/></param>
    /// <exception cref="ArgumentNullException"></exception>
    public Pool(
        IItemFactory<TPoolItem> itemFactory,
        IPreparationStrategy<TPoolItem>? preparationStrategy,
        PoolOptions options)
    {
        this.itemFactory = itemFactory ?? throw new ArgumentNullException(nameof(itemFactory));

        preparationRequired = preparationStrategy is not null;
        this.preparationStrategy = preparationStrategy;

        maxSize = options?.MaxSize ?? Int32.MaxValue;
        initialSize = Math.Min(options?.MinSize ?? 0, maxSize);

        leaseTimeout = options?.LeaseTimeout ?? Timeout.InfiniteTimeSpan;
        idleTimeout = options?.IdleTimeout ?? Timeout.InfiniteTimeSpan;
        preparationTimeout = options?.PreparationTimeout ?? Timeout.InfiniteTimeSpan;

        items = new(CreateItems(initialSize).Select(PoolItem.Create));
    }

    /// <inheritdoc/>>
    public int ItemsAllocated { get; private set; }

    /// <inheritdoc/>>
    public int ItemsAvailable => items.Count;

    /// <inheritdoc/>>
    public int ActiveLeases => ItemsAllocated - ItemsAvailable;

    /// <inheritdoc/>>
    public int QueuedLeases => requests.Count;

    /// <inheritdoc/>>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<TPoolItem> LeaseAsync() => LeaseAsync(CancellationToken.None);

    /// <inheritdoc/>>
    public async ValueTask<TPoolItem> LeaseAsync(CancellationToken cancellationToken)
    {
        if (ThrowIfDisposed().TryAcquireItem(out var item))
        {
            return await EnsurePreparedAsync(item.Item!, cancellationToken);
        }

        var leaseRequest = new LeaseRequest(leaseTimeout, cancellationToken);
        requests.Enqueue(leaseRequest);

        return await leaseRequest.Task;
    }

    /// <inheritdoc/>>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task ReleaseAsync(TPoolItem item) => ReleaseAsync(item, CancellationToken.None);

    /// <inheritdoc/>>
    public async Task ReleaseAsync(
        TPoolItem item,
        CancellationToken cancellationToken)
    {
        if (ThrowIfDisposed().requests.TryDequeue(out var leaseRequest))
        {
            leaseRequest.TaskSetResult(await EnsurePreparedAsync(item, cancellationToken));
            return;
        }

        items.Enqueue(PoolItem.Create(item));
    }

    /// <inheritdoc/>>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task ClearAsync() => ClearAsync(CancellationToken.None);

    /// <inheritdoc/>>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed().EnsureItemsDisposed();

        var tasks = CreateItems(QueuedLeases > initialSize ? QueuedLeases : initialSize)
            .Select(item => ReleaseAsync(item, cancellationToken));

        await Task.WhenAll(tasks);
    }

    private IEnumerable<TPoolItem> CreateItems(int count)
    {
        lock (this)
        {
            ItemsAllocated = count;
            for (var i = 0; i < count; i++)
            {
                yield return itemFactory.CreateItem();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryAcquireItem(out PoolItem item) =>
        TryDequeue(out item) || TryCreateItem(out item);

    private bool TryCreateItem(out PoolItem item)
    {
        item = default;
        lock (this)
        {
            if (ItemsAllocated >= maxSize)
            {
                return false;
            }

            ++ItemsAllocated;
            item = PoolItem.Create(itemFactory.CreateItem());
            return true;
        }
    }

    private bool TryDequeue(out PoolItem item)
    {
        while (items.TryDequeue(out item))
        {
            if (idleTimeout == Timeout.InfiniteTimeSpan || DateTime.UtcNow - item.IdleSince < idleTimeout)
            {
                return true;
            }

            RemoveItem(item.Item!);
        }

        return false;
    }

    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP007:Don't dispose injected", Justification = "it's all private to pool")]
    private void RemoveItem(TPoolItem item)
    {
        lock(this)
        {
            --ItemsAllocated;
        }

        if (!IsPoolItemDisposable)
        {
            (item as IDisposable)?.Dispose();
        }
    }

    private async ValueTask<TPoolItem> EnsurePreparedAsync(
        TPoolItem item,
        CancellationToken cancellationToken)
    {
        if (!preparationRequired)
        {
            return item;
        }

        using var timeoutCts = new CancellationTokenSource(preparationTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token,
            cancellationToken);
        cancellationToken = linkedCts.Token;

        if (await preparationStrategy!.IsReadyAsync(item, cancellationToken))
        {
            return item;
        }

        await preparationStrategy.PrepareAsync(item, cancellationToken);

        return item;
    }

    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP007:Don't dispose injected", Justification = "it's all private to pool")]
    private void EnsureItemsDisposed()
    {
        if (!IsPoolItemDisposable)
        {
            return;
        }

        while (items.TryDequeue(out var item))
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

        while (requests.TryDequeue(out var leaseRequest))
        {
            leaseRequest.Dispose();
        }

        EnsureItemsDisposed();

        disposed = true;
    }
}
