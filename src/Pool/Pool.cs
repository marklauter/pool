﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Pool;

[RequiresDynamicCode("Creating a proxy instance requires generating code at runtime")]
internal sealed class Pool<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>
    : IPool<T>
    , IDisposable
    where T : notnull, IDisposable
{
    private sealed class LeaseRequest
        : IDisposable
    {
        private readonly TaskCompletionSource<T> taskCompletionSource = new();
        private readonly CancellationTokenSource? cancellationTokenSource;
        private bool disposed;

        public LeaseRequest(TimeSpan timeout)
        {
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
    private readonly IServiceScope scope;
    private readonly ConcurrentQueue<T> pool;
    private readonly ConcurrentQueue<LeaseRequest> requests = new();

    public long Allocated { get; private set; }

    public Pool(
        IOptions<PoolOptions> options,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        maxSize = options.Value?.MaxSize ?? Int32.MaxValue;
        var initialSize = options.Value?.InitialSize ?? 0;

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
    public Task<T> LeaseAsync()
    {
        return LeaseAsync(Timeout.InfiniteTimeSpan);
    }

    public async Task<T> LeaseAsync(TimeSpan timeout)
    {
        ThrowIfDisposed();

        using var leaseRequest = new LeaseRequest(timeout);
        if (TryAcquireItem(out var item))
        {
            leaseRequest.SetResult(PoolItemProxy<T>.Create(item, this));
        }
        else
        {
            requests.Enqueue(leaseRequest);
        }

        return await leaseRequest.Task;
    }

    public async Task<T> LeaseAsync(TimeSpan timeout, Func<T, bool> isReady)
    {
        var item = await LeaseAsync(timeout);

        return !isReady(item)
            ? throw new NotReadyException("ready check failed")
            : item;
    }

    public void Release(T item)
    {
        ThrowIfDisposed();

        if (requests.TryDequeue(out var leaseRequest))
        {
            leaseRequest.SetResult(PoolItemProxy<T>.Create(item, this));
        }
        else
        {
            pool.Enqueue(item);
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
