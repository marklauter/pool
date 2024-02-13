namespace Pool;

public sealed class PoolOptions
{
    public int? MinSize { get; set; }
    public int? MaxSize { get; set; }
    public TimeSpan LeaseTimeout { get; set; } = Timeout.InfiniteTimeSpan;
    public TimeSpan ReadyTimeout { get; set; } = Timeout.InfiniteTimeSpan;
}
