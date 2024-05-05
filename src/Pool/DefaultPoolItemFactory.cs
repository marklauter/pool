using Microsoft.Extensions.DependencyInjection;

namespace Pool;

internal sealed class DefaultPoolItemFactory<T>(
    IServiceProvider serviceProvider)
    : IPoolItemFactory<T>
    where T : notnull
{
    private readonly IServiceProvider serviceProvider = serviceProvider
        ?? throw new ArgumentNullException(nameof(serviceProvider));

    public T CreateItem()
    {
        return serviceProvider.GetRequiredService<T>();
    }
}
