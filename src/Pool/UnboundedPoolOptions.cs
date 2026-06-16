using System.ComponentModel.DataAnnotations;

namespace Pool;

/// <summary>
/// Options for configuring an <see cref="UnboundedPool{TPoolItem}"/>.
/// </summary>
/// <remarks>
/// The unbounded pool rents without limit and treats release as an optional optimization, so it
/// honors none of the bounded pool's capacity controls: there is no MaxSize (rent never blocks) and
/// no LeaseTimeout (there is nothing to wait for). The only cap is on <em>retention</em>:
/// <see cref="MaxIdle"/> bounds how many returned items are kept for reuse.
/// </remarks>
public sealed class UnboundedPoolOptions
{
    /// <summary>
    /// MinSize gets or sets the minimum number of idle items to seed the pool with at construction.
    /// </summary>
    /// <remarks>Defaults to zero. Must not be negative. Capped by <see cref="MaxIdle"/> when seeding.</remarks>
    [Range(0, int.MaxValue)]
    public int MinSize { get; set; }

    /// <summary>
    /// MaxIdle gets or sets the maximum number of idle items the pool retains for reuse. Returns
    /// beyond this cap are dropped (and disposed, if the item is <see cref="IDisposable"/>) rather
    /// than pooled. This bounds memory and server-side load without bounding how many items may be
    /// leased concurrently.
    /// </summary>
    /// <remarks>Defaults to 100. Zero means never retain — every return is dropped (pure allocate-on-lease).</remarks>
    [Range(0, int.MaxValue)]
    public int MaxIdle { get; set; } = 100;

    /// <summary>
    /// PreparationTimeout gets or sets the timeout to wait for the ready check and attempt to make ready a pool item before completing the lease request.
    /// </summary>
    /// <remarks>Defaults to Timeout.InfiniteTimeSpan</remarks>
    public TimeSpan PreparationTimeout { get; set; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// IdleTimeout gets or sets the timeout for an item to be idle before it is dropped from the pool. Expired idle items are discarded (and disposed) when encountered on lease.
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
