using Microsoft.Extensions.DependencyInjection;

namespace Pool;

internal sealed class DefaultPoolItemFactory<TPoolItem>(IServiceProvider serviceProvider)
    : IPoolItemFactory<TPoolItem>
    , IDisposable
    where TPoolItem : notnull
{
    // create a service scope so the factory can create scoped services
    private readonly IServiceScope serviceScope = serviceProvider?.CreateScope()
        ?? throw new ArgumentNullException(nameof(serviceProvider));
    private bool disposed;

    public TPoolItem CreateItem() => serviceScope.ServiceProvider.GetRequiredService<TPoolItem>();

    private void Dispose(bool disposing)
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (disposing)
        {
            serviceScope.Dispose();
        }
    }

    public void Dispose() => Dispose(disposing: true);
}
