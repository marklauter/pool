namespace Pool;

public interface IPoolItemFactory<TPoolItem>
    where TPoolItem : notnull
{
    TPoolItem CreateItem();
}
