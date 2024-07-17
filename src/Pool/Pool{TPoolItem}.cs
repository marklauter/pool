using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Pool;

internal sealed class Pool<TPoolItem>
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

    private readonly bool isPoolItemDisposable = typeof(TPoolItem).GetInterface(nameof(IDisposable), true) is not null;
    private readonly int maxSize;
    private readonly int initialSize;
    private readonly bool needsReadyCheck;
    private readonly ConcurrentQueue<TPoolItem> items;
    private readonly ConcurrentQueue<LeaseRequest> requests = new();
    private readonly IItemFactory<TPoolItem> itemFactory;
    private readonly IPreparationStrategy<TPoolItem>? preparationStrategy;
    private readonly TimeSpan leaseTimeout;
    private readonly TimeSpan preparationTimeout;
    private bool disposed;

    public Pool(
        IItemFactory<TPoolItem> itemFactory,
        PoolOptions options)
        : this(itemFactory, null, options)
    { }

    public Pool(
        IItemFactory<TPoolItem> itemFactory,
        IPreparationStrategy<TPoolItem>? preparationStrategy,
        PoolOptions options)
    {
        needsReadyCheck = options?.PreparationRequired ?? preparationStrategy is not null;
        maxSize = options?.MaxSize ?? Int32.MaxValue;
        initialSize = Math.Min(options?.MinSize ?? 0, maxSize);
        leaseTimeout = options?.LeaseTimeout ?? Timeout.InfiniteTimeSpan;
        preparationTimeout = options?.PreparationTimeout ?? Timeout.InfiniteTimeSpan;

        this.itemFactory = itemFactory ?? throw new ArgumentNullException(nameof(itemFactory));
        this.preparationStrategy = needsReadyCheck && preparationStrategy is null
            ? throw new ArgumentNullException(nameof(preparationStrategy))
            : preparationStrategy;

        items = new(CreateItems(initialSize));
    }

    public int ItemsAllocated { get; private set; }
    public int ItemsAvailable => items.Count;
    public int ActiveLeases => ItemsAllocated - ItemsAvailable;
    public int QueuedLeases => requests.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<TPoolItem> LeaseAsync() => LeaseAsync(CancellationToken.None);

    public async ValueTask<TPoolItem> LeaseAsync(CancellationToken cancellationToken)
    {
        if (ThrowIfDisposed().TryAcquireItem(out var item))
        {
            return await EnsurePreparedAsync(item, cancellationToken);
        }

        var leaseRequest = new LeaseRequest(leaseTimeout, cancellationToken);
        requests.Enqueue(leaseRequest);

        return await leaseRequest.Task;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task ReleaseAsync(TPoolItem item) => ReleaseAsync(item, CancellationToken.None);

    public async Task ReleaseAsync(
        TPoolItem item,
        CancellationToken cancellationToken)
    {
        if (ThrowIfDisposed().requests.TryDequeue(out var leaseRequest))
        {
            leaseRequest.TaskSetResult(await EnsurePreparedAsync(item, cancellationToken));
            return;
        }

        items.Enqueue(item);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task ClearAsync() => ClearAsync(CancellationToken.None);

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed().EnsureItemsDisposed();

        var tasks = CreateItems(QueuedLeases > initialSize ? QueuedLeases : initialSize)
            .Select(item => ReleaseAsync(item, cancellationToken));

        await Task.WhenAll(tasks);
    }

    private void EnsureItemsDisposed()
    {
        if (!isPoolItemDisposable)
        {
            return;
        }

        while (items.TryDequeue(out var item))
        {
            (item as IDisposable)?.Dispose();
        }
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
    private bool TryAcquireItem([NotNullWhen(true)] out TPoolItem? item) =>
        items.TryDequeue(out item) || TryCreateItem(out item);

    private bool TryCreateItem([NotNullWhen(true)] out TPoolItem? item)
    {
        item = null;
        lock (this)
        {
            if (ItemsAllocated >= maxSize)
            {
                return false;
            }

            ++ItemsAllocated;
            item = itemFactory.CreateItem();
            return true;
        }
    }

    private async ValueTask<TPoolItem> EnsurePreparedAsync(
        TPoolItem item,
        CancellationToken cancellationToken)
    {
        if (!needsReadyCheck)
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Pool<TPoolItem> ThrowIfDisposed() => disposed
        ? throw new ObjectDisposedException(nameof(Pool<TPoolItem>))
        : this;

    private void Dispose(bool disposing)
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (disposing)
        {
            while (requests.TryDequeue(out var leaseRequest))
            {
                leaseRequest.Dispose();
            }

            EnsureItemsDisposed();
        }
    }

    public void Dispose() => Dispose(disposing: true);
}
