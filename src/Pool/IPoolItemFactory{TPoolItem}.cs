namespace Pool;

/// <summary>
/// IPoolItemFactory creates pool items.
/// </summary>
/// <typeparam name="TPoolItem"></typeparam>
/// <remarks>Implement your own factory, or use the <see cref="DefaultPoolItemFactory{TPoolItem}"/>.</remarks>
public interface IPoolItemFactory<TPoolItem>
    where TPoolItem : notnull
{
    /// <summary>
    /// CreateItem returns a new pool item instance.
    /// </summary>
    /// <returns>TPoolItem</returns>
    TPoolItem CreateItem();
}
