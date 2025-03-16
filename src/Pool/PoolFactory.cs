using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace Pool;

/// <summary>
/// Implementation of <see cref="IPoolFactory{TPoolItem}"/> that creates named pool instances.
/// </summary>
/// <typeparam name="TPoolItem">The type of item contained by the pool.</typeparam>
/// <remarks>
/// Initializes a new instance of the <see cref="PoolFactory{TPoolItem}"/> class.
/// </remarks>
/// <param name="serviceProvider">The service provider used to resolve dependencies.</param>
internal sealed class PoolFactory<TPoolItem>(IServiceProvider serviceProvider) : IPoolFactory<TPoolItem>
    where TPoolItem : class
{
    private readonly IServiceProvider serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly ConcurrentDictionary<string, IPool<TPoolItem>> pools = new();

    /// <inheritdoc/>
    public IPool<TPoolItem> CreatePool(string serviceKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(serviceKey, nameof(serviceKey));
        return pools.GetOrAdd(serviceKey, CreatePoolInstance);
    }

    private IPool<TPoolItem> CreatePoolInstance(string serviceKey) =>
        serviceProvider.GetRequiredKeyedService<IPool<TPoolItem>>(serviceKey)
            ?? throw new InvalidOperationException($"No pool named '{serviceKey}' of type '{typeof(TPoolItem).Name}' has been registered.");
}
