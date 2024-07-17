namespace Pool;

/// <summary>
/// ItemLeaseOptions for configuring pool dependency registration.
/// </summary>
public sealed class ItemLeaseOptions
{
    /// <summary>
    /// Set to true to register the default <see cref="IPreparationStrategy{TPoolItem}"/> implementation.
    /// </summary>
    /// <remarks>Defaults to false.</remarks>
    public bool UseDefaultPreparationStrategy { get; set; }

    /// <summary>
    /// Set to true to register the default <see cref="IItemFactory{TPoolItem}"/> implementation.
    /// </summary>
    /// <remarks>Defaults to false.</remarks>
    public bool UseDefaultFactory { get; set; }
}

