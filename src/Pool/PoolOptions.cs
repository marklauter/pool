namespace Pool;

public sealed class PoolOptions
{
    public int InitialSize { get; set; }
    public int MaxSize { get; set; }
    public TimeSpan WaitTimeout { get; set; }
}
