using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Pool;

internal sealed class Pool<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>
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

    private bool disposed;
    private readonly int maxSize;
    private readonly int initialSize;
    private readonly IServiceScope scope;
    private readonly ConcurrentQueue<T> pool;
    private readonly ConcurrentQueue<LeaseRequest> requests = new();

    public int Allocated { get; private set; }
    public int Available => pool.Count;
    public int ActiveLeases => Allocated - Available;
    public int Backlog => requests.Count;

    public Pool(
        IOptions<PoolOptions> options,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        maxSize = options.Value?.MaxSize ?? Int32.MaxValue;
        initialSize = options.Value?.MinSize ?? 0;

        scope = serviceProvider.CreateScope();

        pool = new ConcurrentQueue<T>(GetRequiredItems(initialSize));
    }

    private T GetRequiredItem()
    {
        var item = scope.ServiceProvider.GetRequiredService<T>();
        ++Allocated;
        return item;
    }

    private IEnumerable<T> GetRequiredItems(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return GetRequiredItem();
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
            leaseRequest.SetResult(item);
        }
        else
        {
            requests.Enqueue(leaseRequest);
        }

        return await leaseRequest.Task;
    }

    public async Task<T> LeaseAsync(
        TimeSpan timeout,
        Func<T, CancellationToken, Task<bool>> isReady,
        Func<T, CancellationToken, Task> makeReady,
        CancellationToken cancellationToken)
    {
        var item = await LeaseAsync(timeout, cancellationToken);

        if (!cancellationToken.IsCancellationRequested &&
            !await isReady(item, cancellationToken))
        {
            await makeReady(item, cancellationToken);
        }

        return item;
    }

    public void Release(T item)
    {
        ThrowIfDisposed();

        if (requests.TryDequeue(out var leaseRequest))
        {
            leaseRequest.SetResult(item);
        }
        else
        {
            pool.Enqueue(item);
        }
    }

    public void Clear()
    {
        ThrowIfDisposed();

        IEnumerable<T> items;
        lock (this)
        {
            Allocated = 0;
            pool.Clear();

            var itemsRequired = Backlog > initialSize
                ? Backlog
                : initialSize;

            items = GetRequiredItems(itemsRequired);
        }

        foreach (var item in items)
        {
            // puts items into the pool or hands them off to queued lease requests
            Release(item);
        }
    }

    private bool TryAcquireItem([MaybeNullWhen(false)] out T item)
    {
        var dequeued = pool.TryDequeue(out item);
        if (dequeued && item is not null)
        {
            return true;
        }

        lock (this)
        {
            if (Allocated < maxSize)
            {
                item = GetRequiredItem();
                return true;
            }
        }

        return false;
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
                scope.Dispose();

                while (requests.TryDequeue(out var leaseRequest))
                {
                    leaseRequest.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
    }
}
