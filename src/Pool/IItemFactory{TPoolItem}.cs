using Pool.DefaultStrategies;

namespace Pool;

/// <summary>
/// IPoolItemFactory creates pool pools.
/// </summary>
/// <typeparam name="TPoolItem"></typeparam>
/// <remarks>Implement your own factory, or use the <see cref="DefaultItemFactory{TPoolItem}"/>.</remarks>
public interface IItemFactory<TPoolItem>
    where TPoolItem : class
{
    /// <summary>
    /// CreateItem returns a new pool item instance.
    /// </summary>
    /// <returns>TPool</returns>
    TPoolItem CreateItem();
}
