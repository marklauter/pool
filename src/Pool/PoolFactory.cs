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
    public IPool<TPoolItem> CreatePool(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name, nameof(name));
        return pools.GetOrAdd(name, CreatePoolInstance);
    }

    private IPool<TPoolItem> CreatePoolInstance(string name) =>
        serviceProvider.GetRequiredKeyedService<IPool<TPoolItem>>(name)
            ?? throw new InvalidOperationException($"No pool named '{name}' of type '{typeof(TPoolItem).Name}' has been registered.");
}
