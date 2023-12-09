using System.Reflection;

namespace Pool;

internal sealed class PoolItemProxy<TItem>
    : DispatchProxy
    , IDisposable
    where TItem : class, IDisposable
{
    private TItem item = default!;
    private IPool<TItem> pool = null!;

    private PoolItemProxy()
    {

    }

    internal static TItem Create(
        TItem item,
        IPool<TItem> pool)
    {
        var itemProxy = Create<TItem, PoolItemProxy<TItem>>();
#pragma warning disable IDISP001 // Dispose created - justification - item is being returned from Create method
        var proxy = itemProxy as PoolItemProxy<TItem>;
#pragma warning restore IDISP001 // Dispose created
#pragma warning disable CS8602 // Dereference of a possibly null reference. - justification - it's impossible to be null
        proxy.item = item;
#pragma warning restore CS8602 // Dereference of a possibly null reference.
        proxy.pool = pool ?? throw new ArgumentNullException(nameof(pool));

        return itemProxy;
    }

    public void Dispose()
    {
        pool.Release(item);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            throw new ArgumentNullException(nameof(targetMethod));
        }

        try
        {
            return targetMethod.Invoke(item, args);
        }
        catch (TargetInvocationException ex)
        {
            // todo: this doesn't work with async..
            // need to check method info for awaitable and check if the task is faulted after invocation
            throw ex?.InnerException ?? throw new ProxyItemInvocationException($"failed during invocation of {targetMethod.Name}", ex);
        }
    }
}
