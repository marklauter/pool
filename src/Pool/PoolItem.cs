//namespace Pool;

//public sealed class PoolItem<T>
//    : IDisposable
//    where T : notnull
//{
//    private readonly IPool<T> pool;

//    internal PoolItem(IPool<T> pool, T item)
//    {
//        this.pool = pool;
//        Item = item;
//    }

//    public T Item { get; }

//    public void Dispose()
//    {
//        pool.Release(Item);
//    }
//}
