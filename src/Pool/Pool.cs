using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace Pool;

internal sealed class Pool<T>
    : IPool<T>
    , IDisposable
    where T : class, IDisposable
{
    private bool disposed;
    private long allocated;
    private readonly int maxSize;
    private readonly TimeSpan waitTimeout;
    private readonly ConcurrentQueue<T> pool;

    public long Size => Interlocked.Read(ref allocated);

    public Pool(
        IOptions<PoolOptions> options,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        maxSize = options.Value.MaxSize;
        allocated = options.Value.InitialSize;
        pool = new ConcurrentQueue<T>(Pool<T>.CreateItems(options.Value.InitialSize));
    }

    public T Lease()
    {
        CheckDisposed();

        var item = AcquireItem();
        return PoolItemProxy<T>.Create(item, this);
    }

    public void Release(T item)
    {
        CheckDisposed();
        ArgumentNullException.ThrowIfNull(item);

        pool.Enqueue(item);
    }

    private T AcquireItem()
    {
        var dequeued = pool.TryDequeue(out var item);
        if (dequeued && item is not null)
        {
            return item;
        }

        var size = Interlocked.Read(ref allocated);
        if (size < maxSize)
        {
            _ = Interlocked.Increment(ref allocated);
            return Pool<T>.CreateItem();
        }
        else
        {
            while (true)
            {
                dequeued = pool.TryDequeue(out item);
                if (dequeued)
                {
                    return item;
                }
            }
        }
    }

    private static T CreateItem()
    {
        return scope.ServiceProvider.GetRequiredService<T>()
            ?? throw new InvalidOperationException($"Could not create an instance of {typeof(T)}");
    }

    private static IEnumerable<T> CreateItems(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return Pool<T>.CreateItem();
        }
    }

    private void CheckDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(Pool<T>));
        }
    }

    private void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;

            if (disposing)
            {
                pool.Clear();
                scope.Dispose();
            }
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
    }

    public T Lease(TimeSpan timeout)
    {
        throw new NotImplementedException();
    }

    public T Lease(TimeSpan timeout, Func<T, bool> isReady)
    {
        throw new NotImplementedException();
    }

    public Task<T> LeaseAsync()
    {
        throw new NotImplementedException();
    }
}
