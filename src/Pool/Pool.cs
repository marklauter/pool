using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Pool;

internal sealed class Pool<T>
    : IPool<T>
    , IDisposable
    where T : notnull
{
    private sealed class LeaseRequest
        : IDisposable
    {
        private readonly TaskCompletionSource<T> taskCompletionSource = new();
        private readonly CancellationTokenSource? cancellationTokenSource;
        private bool disposed;

        public LeaseRequest(TimeSpan timeout, CancellationToken cancellationToken)
        {
            _ = cancellationToken.Register(SetCanceled);

            if (timeout != Timeout.InfiniteTimeSpan)
            {
                cancellationTokenSource = new CancellationTokenSource(timeout);
                _ = cancellationTokenSource.Token.Register(SetCanceled);
            }
        }

        public Task<T> Task => taskCompletionSource.Task;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetResult(T item)
        {
            if (taskCompletionSource.TrySetResult(item))
            {
                Dispose();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetCanceled()
        {
            if (taskCompletionSource.TrySetCanceled())
            {
                Dispose();
            }
        }

        private void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    if (cancellationTokenSource is not null)
                    {
                        cancellationTokenSource.Cancel();
                        cancellationTokenSource.Dispose();
                    }
                }

                disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
        }
    }

    private readonly bool disposeRequired = typeof(T)
        .GetInterface(nameof(IDisposable), true) is not null;
    private bool disposed;
    private readonly int maxSize;
    private readonly int initialSize;
    private readonly ConcurrentQueue<T> pool;
    private readonly ConcurrentQueue<LeaseRequest> requests = new();
    private readonly IPoolItemFactory<T> itemFactory;

    public int Allocated { get; private set; }
    public int Available => pool.Count;
    public int ActiveLeases => Allocated - Available;
    public int Backlog => requests.Count;

    public Pool(
        IPoolItemFactory<T> itemFactory,
        IOptions<PoolOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        maxSize = options.Value?.MaxSize ?? Int32.MaxValue;
        initialSize = options.Value?.MinSize ?? 0;
        initialSize = initialSize > maxSize ? maxSize : initialSize;

        this.itemFactory = itemFactory ?? throw new ArgumentNullException(nameof(itemFactory));
        pool = new ConcurrentQueue<T>(CreateItems(initialSize));
    }

    private bool TryCreateItem([MaybeNullWhen(false)] out T item)
    {
        item = default;
        lock (this)
        {
            if (Allocated < maxSize)
            {
                ++Allocated;
                item = itemFactory.Create();
                return true;
            }
        }

        return false;
    }

    private IEnumerable<T> CreateItems(int count)
    {
        lock (this)
        {
            Allocated = count;
            for (var i = 0; i < count; i++)
            {
                yield return itemFactory.Create();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task<T> LeaseAsync(CancellationToken cancellationToken)
    {
        return LeaseAsync(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    public async Task<T> LeaseAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        using var leaseRequest = new LeaseRequest(timeout, cancellationToken);
        if (TryAcquireItem(out var item))
        {
            if (!await itemFactory.IsReadyAsync(item, cancellationToken))
            {
                await itemFactory.MakeReadyAsync(item, cancellationToken);
            }

            leaseRequest.SetResult(item);
        }
        else
        {
            requests.Enqueue(leaseRequest);
        }

        return await leaseRequest.Task;
    }

    public async Task ReleaseAsync(T item, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (requests.TryDequeue(out var leaseRequest))
        {
            if (!await itemFactory.IsReadyAsync(item, cancellationToken))
            {
                await itemFactory.MakeReadyAsync(item, cancellationToken);
            }

            leaseRequest.SetResult(item);
        }
        else
        {
            pool.Enqueue(item);
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        pool.Clear();

        var itemsRequired = Backlog > initialSize
            ? Backlog
            : initialSize;

        var items = CreateItems(itemsRequired);

        var tasks = new List<Task>(items.Count());
        foreach (var item in items)
        {
            // puts items into the pool or hands them off to queued lease requests
            tasks.Add(ReleaseAsync(item, cancellationToken));
        }

        await Task.WhenAll(tasks);
    }

    private bool TryAcquireItem([MaybeNullWhen(false)] out T item)
    {
        var dequeued = pool.TryDequeue(out item);
        return dequeued && item is not null
            || TryCreateItem(out item);
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
                    while (pool.TryDequeue(out var item))
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
