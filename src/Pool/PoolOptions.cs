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
    public int MinSize { get; init; }

    /// <summary>
    /// MaxSize gets or sets the maximum number of items in the pool.
    /// </summary>
    /// <remarks>Defaults to Int32.MaxValue</remarks>
    public int MaxSize { get; init; } = Int32.MaxValue;

    /// <summary>
    /// LeaseTimeout gets or sets the timeout for leasing an item from the pool.
    /// </summary>
    /// <remarks>Defaults to Timeout.InfiniteTimeSpan</remarks>
    public TimeSpan LeaseTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// ReadyTimeout gets or sets the timeout to wait for the ready check and attempt to make ready a pool item before completing the lease request.
    /// </summary>
    /// <remarks>Defaults to Timeout.InfiniteTimeSpan</remarks>
    public TimeSpan ReadyTimeout { get; init; } = Timeout.InfiniteTimeSpan;
}
