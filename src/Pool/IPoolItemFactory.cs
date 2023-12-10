namespace Pool;

public interface IPoolItemFactory<T>
    where T : notnull
{
    T Create();

    Task<bool> IsReadyAsync(T item, CancellationToken cancellationToken);

    Task MakeReadyAsync(T item, CancellationToken cancellationToken);
}
