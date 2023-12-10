using System.Reflection;

namespace Pool;

internal sealed class PoolItemProxy<T>
    : DispatchProxy
    , IDisposable
    where T : notnull, IDisposable
{
    private T item = default!;
    private IPool<T> pool = null!;

    private PoolItemProxy()
    {

    }

    internal static T Create(
        T item,
        IPool<T> pool)
    {
        var itemProxy = Create<T, PoolItemProxy<T>>();
#pragma warning disable IDISP001 // Dispose created - justification - item is being returned from Create method
        var proxy = itemProxy as PoolItemProxy<T>;
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
        ArgumentNullException.ThrowIfNull(targetMethod);

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
