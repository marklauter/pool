namespace Pool;

public interface IReadyCheck<TPoolItem>
    where TPoolItem : notnull
{
    Task<bool> IsReadyAsync(TPoolItem item, CancellationToken cancellationToken);
    Task MakeReadyAsync(TPoolItem item, CancellationToken cancellationToken);
}
