namespace Pool.DependencyInjection;

/// <summary>
/// PoolRegistrationOptions for configuring pool dependency registration.
/// </summary>
public sealed class PoolRegistrationOptions
{
    /// <summary>
    /// Set to true to register the default <see cref="IPoolItemReadyCheck{TPoolItem}"/> implementation.
    /// </summary>
    /// <remarks>Defaults to false.</remarks>
    public bool UseDefaultReadyCheck { get; set; }

    /// <summary>
    /// Set to true to register the default <see cref="IPoolItemFactory{TPoolItem}"/> implementation.
    /// </summary>
    /// <remarks>Defaults to false.</remarks>
    public bool UseDefaultFactory { get; set; }
}

