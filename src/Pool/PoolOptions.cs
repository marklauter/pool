namespace Pool;

public sealed class PoolOptions
{
    public int MinSize { get; init; }
    public int MaxSize { get; init; } = Int32.MaxValue;
    public TimeSpan LeaseTimeout { get; init; } = Timeout.InfiniteTimeSpan;
    public TimeSpan ReadyTimeout { get; init; } = Timeout.InfiniteTimeSpan;
}
