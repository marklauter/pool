using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Pool;

internal sealed class Pool<TPoolItem>
    : IPool<TPoolItem>
    , IDisposable
    where TPoolItem : notnull
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
    private readonly IPoolItemFactory<TPoolItem> itemFactory;
    private readonly IPoolItemReadyCheck<TPoolItem>? readyCheck;
    private readonly TimeSpan leaseTimeout;
    private readonly TimeSpan readyTimeout;
    private bool disposed;

    public Pool(
        IPoolItemFactory<TPoolItem> itemFactory,
        PoolOptions options)
        : this(itemFactory, null, options)
    { }

    public Pool(
        IPoolItemFactory<TPoolItem> itemFactory,
        IPoolItemReadyCheck<TPoolItem>? readyCheck,
        PoolOptions options)
    {
        needsReadyCheck = options?.NeedsReadyCheck ?? readyCheck is not null;
        maxSize = options?.MaxSize ?? Int32.MaxValue;
        initialSize = Math.Min(options?.MinSize ?? 0, maxSize);
        leaseTimeout = options?.LeaseTimeout ?? Timeout.InfiniteTimeSpan;
        readyTimeout = options?.ReadyTimeout ?? Timeout.InfiniteTimeSpan;

        this.itemFactory = itemFactory ?? throw new ArgumentNullException(nameof(itemFactory));
        this.readyCheck = needsReadyCheck && readyCheck is null
            ? throw new ArgumentNullException(nameof(readyCheck))
            : readyCheck;

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
        ThrowIfDisposed();

        if (TryAcquireItem(out var item))
        {
            if (needsReadyCheck)
            {
                await ReadyCheckAsync(readyCheck!, readyTimeout, item, cancellationToken);
            }

            return item;
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
        ThrowIfDisposed();

        if (requests.TryDequeue(out var leaseRequest))
        {
            if (needsReadyCheck)
            {
                await ReadyCheckAsync(readyCheck!, readyTimeout, item, cancellationToken);
            }

            leaseRequest.TaskSetResult(item);
            return;
        }

        items.Enqueue(item);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task ClearAsync() => ClearAsync(CancellationToken.None);

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        ClearQueue();

        var itemsRequired = QueuedLeases > initialSize
            ? QueuedLeases
            : initialSize;

        var items = CreateItems(itemsRequired);

        var tasks = new List<Task>(items.Count());
        foreach (var item in items)
        {
            // enqueues items or hands them off to backlogged lease requests
            tasks.Add(ReleaseAsync(item, cancellationToken));
        }

        await Task.WhenAll(tasks);
    }

    private void ClearQueue()
    {
        if (!isPoolItemDisposable)
        {
            items.Clear();
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
    private bool TryAcquireItem([MaybeNullWhen(false)] out TPoolItem item)
    {
        var dequeued = items.TryDequeue(out item);
        return dequeued && item is not null || TryCreateItem(out item);
    }

    private bool TryCreateItem([MaybeNullWhen(false)] out TPoolItem item)
    {
        item = default;
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

    private static async Task ReadyCheckAsync(
        IPoolItemReadyCheck<TPoolItem> readyCheck,
        TimeSpan readyTimeout,
        TPoolItem item,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(readyTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token,
            cancellationToken);

        cancellationToken = linkedCts.Token;

        if (!await readyCheck.IsReadyAsync(item, cancellationToken))
        {
            await readyCheck.MakeReadyAsync(item, cancellationToken);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
#if NET7_0_OR_GREATER
#pragma warning disable IDE0022 // Use expression body for method
        ObjectDisposedException.ThrowIf(disposed, nameof(Pool<TPoolItem>));
#pragma warning restore IDE0022 // Use expression body for method
#elif NET6_0_OR_GREATER                                                      
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(Pool<TPoolItem>));
        }
#endif
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
            while (requests.TryDequeue(out var leaseRequest))
            {
                leaseRequest.Dispose();
            }

            ClearQueue();
        }
    }

    public void Dispose() => Dispose(disposing: true);
}
