namespace Pool;

/// <summary>
/// Factory for creating instances of <see cref="IPool{TPoolItem}"/> with specific names.
/// Allows creation of multiple pools of the same type with different configurations.
/// </summary>
/// <typeparam name="TPoolItem">The type of item contained by the pool.</typeparam>
public interface IPoolFactory<TPoolItem>
    where TPoolItem : class
{
    /// <summary>
    /// Creates an instance of <see cref="IPool{TPoolItem}"/> with the specified name.
    /// </summary>
    /// <param name="name">The name of the pool to create.</param>
    /// <returns>An instance of <see cref="IPool{TPoolItem}"/> that was registered with the specified name.</returns>
    /// <exception cref="ArgumentException">Thrown when no pool with the specified name has been registered.</exception>
    IPool<TPoolItem> CreatePool(string name);
}
