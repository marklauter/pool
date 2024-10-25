using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Pool;

/// <inheritdoc/>>
public sealed class PoolMap<TKey, TPool>
    : IPoolMap<TKey, TPool>
    , IDisposable
    where TKey : class
    where TPool : class
{

    private static readonly bool IsPoolItemDisposable = typeof(TPool).GetInterface(nameof(IDisposable), true) is not null;

    private readonly ConcurrentDictionary<TKey, Pool<TPool>> pools = new();
    private readonly PoolOptions poolOptions;
    private readonly bool preparationRequired;
    private readonly IItemFactory<TPool> itemFactory;
    private readonly IPreparationStrategy<TPool>? preparationStrategy;
    private readonly IPreparationStrategy<TKey, TPool>? connectionpreparationStrategy;
    private readonly TimeSpan preparationTimeout;
    private bool disposed;

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="itemFactory"></param>
    /// <param name="options"></param>
    public PoolMap(
        IItemFactory<TPool> itemFactory,
        PoolOptions options)
        : this(itemFactory, null, null, options)
    { }

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="itemFactory"><see cref="IItemFactory{TPoolItem}"/></param>
    /// <param name="preparationStrategy"><see cref="IPreparationStrategy{TPoolItem}"/></param>
    /// <param name="connectionpreparationStrategy"><see cref="IPreparationStrategy{TConnectionKeyTPoolItem}"/></param>
    /// <param name="options"><see cref="PoolOptions"/></param>
    /// <exception cref="ArgumentNullException"></exception>
    public PoolMap(
        IItemFactory<TPool> itemFactory,
        IPreparationStrategy<TPool>? preparationStrategy,
        IPreparationStrategy<TKey, TPool>? connectionpreparationStrategy,
        PoolOptions options)
    {
        this.itemFactory = itemFactory ?? throw new ArgumentNullException(nameof(itemFactory));

        preparationRequired = preparationStrategy is not null;
        this.preparationStrategy = preparationStrategy;
        this.connectionpreparationStrategy = connectionpreparationStrategy;
        poolOptions = options;
        preparationTimeout = options?.PreparationTimeout ?? Timeout.InfiniteTimeSpan;
    }

    /// <inheritdoc/>>
    public int UniqueLeases { get; private set; }

    /// <inheritdoc/>>
    public int QueuedLeases => pools.Select(x => x.Value).Select(x => x.QueuedLeases).Sum();

    /// <inheritdoc/>>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<TPool> LeaseAsync(TKey key) => LeaseAsync(key, CancellationToken.None);

    /// <inheritdoc/>>
    public async ValueTask<TPool> LeaseAsync(TKey key, CancellationToken cancellationToken)
    {
        _ = ThrowIfDisposed().TryAcquireItem(key, out var item);

        var pooItem = await LeasePoolItemAsync(item, cancellationToken);
        return await EnsurePreparedAsync(key, pooItem, cancellationToken);
    }

    /// <inheritdoc/>>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task ReleaseAsync(TKey key, TPool pool) => ReleaseAsync(key, pool, CancellationToken.None);

    /// <inheritdoc/>>
    public async Task ReleaseAsync(
        TKey key,
        TPool pool,
        CancellationToken cancellationToken) => await pools[key].ReleaseAsync(pool, cancellationToken);

    /// <inheritdoc/>>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task ClearAsync() => ClearAsync(CancellationToken.None);

    /// <inheritdoc/>>
    public async Task ClearAsync(CancellationToken cancellationToken)
        => await Task.WhenAll(pools.Select(x => x.Value.ClearAsync(cancellationToken)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryAcquireItem(TKey connectionKey, out Pool<TPool> item) =>
        TryGetItem(connectionKey, out item) || TryCreateItem(connectionKey, out item);

    private bool TryCreateItem(TKey connectionKey, out Pool<TPool> item)
    {
        lock (this)
        {
            item = new Pool<TPool>(itemFactory, preparationStrategy, poolOptions);
            ++UniqueLeases;
            _ = pools.TryAdd(connectionKey, item);
            return true;
        }
    }

    private bool TryGetItem(TKey connectionKey, out Pool<TPool> item) => pools.TryGetValue(connectionKey, out item!);

    private static async ValueTask<TPool> LeasePoolItemAsync(Pool<TPool> pool, CancellationToken cancellationToken)
        => await pool.LeaseAsync(cancellationToken);

    private async ValueTask<TPool> EnsurePreparedAsync(
        TKey connectionKey,
        TPool item,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(preparationTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token,
            cancellationToken);
        cancellationToken = linkedCts.Token;

        if (await connectionpreparationStrategy!.IsReadyAsync(connectionKey, item, cancellationToken))
        {
            return item;
        }

        await connectionpreparationStrategy.PrepareAsync(connectionKey, item, cancellationToken);

        return item;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PoolMap<TKey, TPool> ThrowIfDisposed() => disposed
        ? throw new ObjectDisposedException(nameof(PoolMap<TKey, TPool>))
        : this;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
    }
}
