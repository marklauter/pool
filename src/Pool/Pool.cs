using Microsoft.Extensions.Options;
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
            if (!disposed)
            {
                disposed = true;

                if (disposing)
                {
                    if (timeoutTokenSource is not null)
                    {
                        // Canceling unblocks any awaiters.
                        // Cancel() will either call SetCanceled() directly if
                        // there is no linked token source,
                        // or it will trigger Cancel() on the linked token source,
                        // so the linked token source will not need to be canceled explicitly.
                        timeoutTokenSource.Cancel();
                        timeoutTokenSource.Dispose();
                    }

                    linkedTokenSource?.Dispose();
                }
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
        }
    }

    private readonly bool disposeRequired = typeof(TPoolItem)
        .GetInterface(nameof(IDisposable), true) is not null;
    private bool disposed;
    private readonly int maxSize;
    private readonly int initialSize;
    private readonly ConcurrentQueue<TPoolItem> items;
    private readonly ConcurrentQueue<LeaseRequest> requests = new();
    private readonly IPoolItemFactory<TPoolItem> itemFactory;
    private readonly TimeSpan leaseTimeout;
    private readonly TimeSpan readyTimeout;

    public int ItemsAllocated { get; private set; }
    public int ItemsAvailable => items.Count;
    public int ActiveLeases => ItemsAllocated - ItemsAvailable;
    public int LeaseBacklog => requests.Count;

    public Pool(
        IPoolItemFactory<TPoolItem> itemFactory,
        IOptions<PoolOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        leaseTimeout = options.Value?.LeaseTimeout ?? Timeout.InfiniteTimeSpan;
        readyTimeout = options.Value?.ReadyTimeout ?? Timeout.InfiniteTimeSpan;
        maxSize = options.Value?.MaxSize ?? Int32.MaxValue;
        initialSize = options.Value?.MinSize ?? 0;
        initialSize = initialSize > maxSize
            ? maxSize
            : initialSize;

        this.itemFactory = itemFactory ?? throw new ArgumentNullException(nameof(itemFactory));
        items = new ConcurrentQueue<TPoolItem>(CreateItems(initialSize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task<TPoolItem> LeaseAsync()
    {
        return LeaseAsync(CancellationToken.None);
    }

    public async Task<TPoolItem> LeaseAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (TryAcquireItem(out var item))
        {
            await ReadyCheckAsync(item, cancellationToken);
            return item;
        }

        var leaseRequest = new LeaseRequest(leaseTimeout, cancellationToken);
        requests.Enqueue(leaseRequest);
        return await leaseRequest.Task;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task ReleaseAsync(TPoolItem item)
    {
        return ReleaseAsync(item, CancellationToken.None);
    }

    public async Task ReleaseAsync(
        TPoolItem item,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (requests.TryDequeue(out var leaseRequest))
        {
            await ReadyCheckAsync(item, cancellationToken);
            leaseRequest.TaskSetResult(item);
            return;
        }

        items.Enqueue(item);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task ClearAsync()
    {
        return ClearAsync(CancellationToken.None);
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        this.items.Clear();

        var itemsRequired = LeaseBacklog > initialSize
            ? LeaseBacklog
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

    private bool TryCreateItem([MaybeNullWhen(false)] out TPoolItem item)
    {
        item = default;
        lock (this)
        {
            if (ItemsAllocated < maxSize)
            {
                ++ItemsAllocated;
                item = itemFactory.Create();
                return true;
            }
        }

        return false;
    }

    private IEnumerable<TPoolItem> CreateItems(int count)
    {
        lock (this)
        {
            ItemsAllocated = count;
            for (var i = 0; i < count; i++)
            {
                yield return itemFactory.Create();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryAcquireItem([MaybeNullWhen(false)] out TPoolItem item)
    {
        var dequeued = items.TryDequeue(out item);
        return dequeued && item is not null
            || TryCreateItem(out item);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async Task ReadyCheckAsync(
        TPoolItem item,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(readyTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token,
            cancellationToken);

        cancellationToken = linkedCts.Token;

        if (!await itemFactory.IsReadyAsync(item, cancellationToken))
        {
            await itemFactory.MakeReadyAsync(item, cancellationToken);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;

            if (disposing)
            {
                while (requests.TryDequeue(out var leaseRequest))
                {
                    leaseRequest.Dispose();
                }

                if (disposeRequired)
                {
                    while (items.TryDequeue(out var item))
                    {
                        (item as IDisposable)?.Dispose();
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
    }
}
