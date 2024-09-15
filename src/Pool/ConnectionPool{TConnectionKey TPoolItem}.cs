using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Pool;

/// <inheritdoc/>>
public sealed class ConnectionPool<TConnectionKey, TPoolItem>
    : IConnectionPool<TConnectionKey, TPoolItem>
    , IDisposable
    where TConnectionKey : class
    where TPoolItem : class
{

    private static readonly bool IsPoolItemDisposable = typeof(TPoolItem).GetInterface(nameof(IDisposable), true) is not null;

    private readonly ConcurrentDictionary<TConnectionKey, Pool<TPoolItem>> items = new();
    private readonly PoolOptions poolOptions;
    private readonly bool preparationRequired;
    private readonly IItemFactory<TPoolItem> itemFactory;
    private readonly IPreparationStrategy<TPoolItem>? preparationStrategy;
    private readonly IPreparationStrategy<TConnectionKey, TPoolItem>? connectionpreparationStrategy;
    private readonly TimeSpan preparationTimeout;
    private bool disposed;

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="itemFactory"></param>
    /// <param name="options"></param>
    public ConnectionPool(
        IItemFactory<TPoolItem> itemFactory,
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
    public ConnectionPool(
        IItemFactory<TPoolItem> itemFactory,
        IPreparationStrategy<TPoolItem>? preparationStrategy,
        IPreparationStrategy<TConnectionKey, TPoolItem>? connectionpreparationStrategy,
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
    public int ItemsAvailable => items.Count;

    /// <inheritdoc/>>
    public int QueuedLeases => items.Select(x => x.Value).Select(x => x.QueuedLeases).Sum();

    /// <inheritdoc/>>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<TPoolItem> LeaseAsync(TConnectionKey connectionKey) => LeaseAsync(connectionKey, CancellationToken.None);

    /// <inheritdoc/>>
    public async ValueTask<TPoolItem> LeaseAsync(TConnectionKey connectionKey, CancellationToken cancellationToken)
    {
        _ = ThrowIfDisposed().TryAcquireItem(connectionKey, out var item);

        var pooItem = await LeasePoolItemAsync(item, cancellationToken);
        return await EnsurePreparedAsync(connectionKey, pooItem, cancellationToken);
    }

    /// <inheritdoc/>>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task ReleaseAsync(TConnectionKey connectionKey, TPoolItem item) => ReleaseAsync(connectionKey, item, CancellationToken.None);

    /// <inheritdoc/>>
    public async Task ReleaseAsync(
        TConnectionKey connectionKey,
        TPoolItem item,
        CancellationToken cancellationToken) => await items[connectionKey].ReleaseAsync(item, cancellationToken);

    /// <inheritdoc/>>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task ClearAsync() => ClearAsync(CancellationToken.None);

    /// <inheritdoc/>>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        var tasks = items.Select(x => x.Value.ClearAsync(cancellationToken));

        await Task.WhenAll(tasks);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryAcquireItem(TConnectionKey connectionKey, out Pool<TPoolItem> item) =>
        TryGetItem(connectionKey, out item) || TryCreateItem(connectionKey, out item);

    private bool TryCreateItem(TConnectionKey connectionKey, out Pool<TPoolItem> item)
    {
        lock (this)
        {
            item = new Pool<TPoolItem>(itemFactory, preparationStrategy, poolOptions);
            ++UniqueLeases;
            _ = items.TryAdd(connectionKey, item);
            return true;
        }
    }

    private bool TryGetItem(TConnectionKey connectionKey, out Pool<TPoolItem> item) => items.TryGetValue(connectionKey, out item!);

    private static async ValueTask<TPoolItem> LeasePoolItemAsync(Pool<TPoolItem> pool, CancellationToken cancellationToken)
        => await pool.LeaseAsync(cancellationToken);

    private async ValueTask<TPoolItem> EnsurePreparedAsync(
        TConnectionKey connectionKey,
        TPoolItem item,
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
    private ConnectionPool<TConnectionKey, TPoolItem> ThrowIfDisposed() => disposed
        ? throw new ObjectDisposedException(nameof(ConnectionPool<TConnectionKey, TPoolItem>))
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
