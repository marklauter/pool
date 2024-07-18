namespace Pool;

/// <summary>
/// PoolOptions for configuring the pool.
/// </summary>
public sealed class PoolOptions
{
    /// <summary>
    /// MinSize gets or sets the minimum number of items in the pool.
    /// </summary>
    /// <remarks>Defaults to zero.</remarks>
    public int MinSize { get; set; }

    /// <summary>
    /// MaxSize gets or sets the maximum number of items in the pool.
    /// </summary>
    /// <remarks>Defaults to Int32.MaxValue</remarks>
    public int MaxSize { get; set; } = Int32.MaxValue;

    /// <summary>
    /// LeaseTimeout gets or sets the timeout for leasing an item from the pool.
    /// </summary>
    /// <remarks>Defaults to Timeout.InfiniteTimeSpan</remarks>
    public TimeSpan LeaseTimeout { get; set; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// ReadyTimeout gets or sets the timeout to wait for the ready check and attempt to make ready a pool item before completing the lease request.
    /// </summary>
    /// <remarks>Defaults to Timeout.InfiniteTimeSpan</remarks>
    public TimeSpan PreparationTimeout { get; set; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// IdleTimeout gets or sets the timeout for an item to be idle before it is removed from the pool.
    /// </summary>
    /// <remarks>Defaults to Timeout.InfiniteTimeSpan</remarks>
    public TimeSpan IdleTimeout { get; set; } = Timeout.InfiniteTimeSpan;

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
